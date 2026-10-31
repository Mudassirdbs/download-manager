using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Core;

/// <summary>One quality the user can pick; <see cref="Selector"/> is a yt-dlp <c>-f</c> expression.</summary>
public sealed record FormatChoice(string Selector, string Label, string Ext, long? Size, bool AudioOnly);

public sealed record MediaInfo(string Title, string? Extractor, double? Duration, List<FormatChoice> Choices);

public static class YtDlp
{
    /// <summary>
    /// Asks yt-dlp what the page offers (<c>-J</c>, no download). Signed-in YouTube requests lose their formats when
    /// no JavaScript runtime can solve the site's challenge, so a page with no formats is retried without cookies.
    /// </summary>
    public static async Task<MediaInfo> ResolveAsync(Tools tools, string url, RequestContext ctx, CancellationToken token)
    {
        try
        {
            return await LookUpAsync(tools, url, ctx, token);
        }
        catch (InvalidOperationException ex) when (ShouldRetryWithoutCookies(ex.Message, ctx))
        {
            Log.Warn($"yt-dlp found no formats with cookies for {Log.Redact(url)}; retrying without them");
            return await LookUpAsync(tools, url, WithoutCookies(ctx), token);
        }
    }

    internal static bool ShouldRetryWithoutCookies(string error, RequestContext ctx) =>
        (ctx.CookieJar is { Count: > 0 } || !string.IsNullOrEmpty(ctx.Cookies))
        && (error.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No video formats found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Only images are available", StringComparison.OrdinalIgnoreCase));

    internal static RequestContext WithoutCookies(RequestContext ctx) => ctx with { Cookies = null, CookieJar = null };

    /// <summary>yt-dlp enables only Deno by default and looks for it on PATH; name the runtime we found.</summary>
    internal static void AddJsRuntime(List<string> args, Tools tools)
    {
        if (tools.Deno is { } deno) args.AddRange(["--js-runtimes", "deno:" + deno]);
        else if (tools.Node is { } node) args.AddRange(["--js-runtimes", "node:" + node]);
    }

    static async Task<MediaInfo> LookUpAsync(Tools tools, string url, RequestContext ctx, CancellationToken token)
    {
        var ytDlp = tools.YtDlp ?? throw new InvalidOperationException("yt-dlp is not installed");
        using var cookies = CookieFile.Create(url, ctx);
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--no-progress", "--color", "never", "--encoding", "utf-8" };
        AddJsRuntime(args, tools);
        AddContext(args, ctx, cookies);
        args.Add(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var result = await ProcessRunner.RunAsync(ytDlp, args, timeout.Token, captureStdout: true);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(ProcessRunner.Summarize(result.StderrTail, "yt-dlp could not read this page"));
        using var json = JsonDocument.Parse(result.Stdout);
        return Describe(json.RootElement);
    }

    /// <summary>Reduces yt-dlp's raw format table to: best, one entry per resolution, and audio only.</summary>
    public static MediaInfo Describe(JsonElement root)
    {
        var title = Str(root, "title") ?? "video";
        if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("Live streams are not supported yet");

        var formats = root.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Where(Usable).ToList()
            : [];
        var choices = new List<FormatChoice>();
        if (formats.Count == 0)
        {
            choices.Add(new FormatChoice("b", "Best available", Str(root, "ext") ?? "mp4", Size(root), false));
            return new MediaInfo(title, Str(root, "extractor_key"), Num(root, "duration"), choices);
        }

        // Sites such as X leave codecs out: a missing vcodec with a height is video, and a missing acodec may be audio.
        static bool HasVideo(JsonElement e) => Str(e, "vcodec") is var v && v != "none" && (v is not null || Num(e, "height") is > 0);
        static bool HasAudio(JsonElement e) => Str(e, "acodec") != "none";

        // H.264/AAC play in every Windows player; VP9, AV1 and Opus need extra codecs in some.
        var audioOnly = formats.Where(e => HasAudio(e) && !HasVideo(e)).ToList();
        var aac = Best(audioOnly.Where(IsAac));
        var bestAudio = aac.ValueKind != JsonValueKind.Undefined ? aac : Best(audioOnly);
        var audioSize = bestAudio.ValueKind == JsonValueKind.Undefined ? null : Size(bestAudio);
        var byHeight = formats.Where(HasVideo).Where(e => Num(e, "height") is > 0)
            .GroupBy(e => (int)Num(e, "height")!.Value).OrderByDescending(g => g.Key).ToList();

        // A complete MP4 with undeclared codecs: H.264 in practice, and often far better than the same height's HLS.
        static JsonElement DirectFile(IGrouping<int, JsonElement> g) =>
            Best(g.Where(e => Str(e, "vcodec") is null && Str(e, "ext") == "mp4" && HasAudio(e)));

        // The format the selector for this height will most likely pick: H.264 when that height has it.
        static bool PrefersDirectFile(IGrouping<int, JsonElement> g)
        {
            var file = DirectFile(g);
            if (file.ValueKind == JsonValueKind.Undefined) return false;
            var h264 = Best(g.Where(IsH264));
            return h264.ValueKind == JsonValueKind.Undefined || Bitrate(file) > Bitrate(h264);
        }

        JsonElement PickAt(IGrouping<int, JsonElement> g)
        {
            if (PrefersDirectFile(g)) return DirectFile(g);
            var h264 = Best(g.Where(IsH264));
            return h264.ValueKind != JsonValueKind.Undefined ? h264 : Best(g);
        }

        long? SizeOf(JsonElement pick)
        {
            var video = Size(pick);
            return video is null ? null : HasAudio(pick) ? video : video + (audioSize ?? 0);
        }

        FormatChoice ChoiceAt(IGrouping<int, JsonElement> g, string? labelPrefix)
        {
            var pick = PickAt(g);
            var fps = Num(pick, "fps") ?? 0;
            var named = Str(pick, "vcodec") is { } codec && !IsH264(pick) ? $" · {CodecName(codec)}" : "";
            var label = $"{g.Key}p" + (fps > 30 ? $"{Math.Round(fps)}" : "") + named;
            if (labelPrefix is not null) label = $"{labelPrefix} ({label})";
            var selector = PrefersDirectFile(g) ? $"b[height={g.Key}][ext=mp4]/{SelectorFor(g.Key)}" : SelectorFor(g.Key);
            return new FormatChoice(selector, label, "mp4", SizeOf(pick), false);
        }

        if (byHeight.Count > 0)
        {
            choices.Add(ChoiceAt(byHeight[0], "Best quality"));
            choices.AddRange(byHeight.Select(g => ChoiceAt(g, null)));
        }
        else
        {
            choices.Add(new FormatChoice("bv*+ba/b", "Best quality", "mp4", null, false));
        }
        if (bestAudio.ValueKind != JsonValueKind.Undefined)
            choices.Add(new FormatChoice("ba[acodec^=mp4a]/ba/b", "Audio only", Str(bestAudio, "ext") ?? "m4a", audioSize, true));
        return new MediaInfo(title, Str(root, "extractor_key"), Num(root, "duration"), choices);
    }

    /// <summary>
    /// H.264 + AAC at exactly this height, else H.264 with any audio, else the best codec at or below it.
    /// The exact-height match keeps a "2160p" choice from quietly becoming 1080p H.264 on sites that offer
    /// 4K only as VP9/AV1.
    /// </summary>
    static string SelectorFor(int height) =>
        $"bv*[height={height}][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height={height}][vcodec^=avc1]+ba/b[height={height}][vcodec^=avc1]" +
        $"/bv*[height<={height}]+ba/b[height<={height}]";

    static bool IsH264(JsonElement e) => Str(e, "vcodec")?.StartsWith("avc1", StringComparison.Ordinal) == true;
    static bool IsAac(JsonElement e) => Str(e, "acodec")?.StartsWith("mp4a", StringComparison.Ordinal) == true;
    static double Bitrate(JsonElement e) => Num(e, "tbr") ?? 0;

    /// <summary>The highest-bitrate format, or an undefined element when there is none (MaxBy throws for structs).</summary>
    static JsonElement Best(IEnumerable<JsonElement> formats)
    {
        var list = formats.ToList();
        return list.Count == 0 ? default : list.MaxBy(Bitrate);
    }

    static string CodecName(string vcodec) => vcodec switch
    {
        _ when vcodec.StartsWith("vp09", StringComparison.Ordinal) || vcodec.StartsWith("vp9", StringComparison.Ordinal) => "VP9",
        _ when vcodec.StartsWith("av01", StringComparison.Ordinal) => "AV1",
        _ when vcodec.StartsWith("hev1", StringComparison.Ordinal) || vcodec.StartsWith("hvc1", StringComparison.Ordinal) => "HEVC",
        _ => vcodec.Split('.')[0].ToUpperInvariant(),
    };

    static bool Usable(JsonElement e) =>
        !(e.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True)
        && Str(e, "protocol") != "mhtml" // storyboard thumbnails
        && !(Str(e, "vcodec") == "none" && Str(e, "acodec") == "none");

    internal static void AddContext(List<string> args, RequestContext ctx, CookieFile? cookies)
    {
        if (cookies is not null) args.AddRange(["--cookies", cookies.Path]);
        if (!string.IsNullOrEmpty(ctx.UserAgent)) args.AddRange(["--user-agent", ctx.UserAgent]);
        if (!string.IsNullOrEmpty(ctx.Referrer)) args.AddRange(["--referer", ctx.Referrer]);
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static long? Size(JsonElement e) => (long?)(Num(e, "filesize") ?? Num(e, "filesize_approx"));
}

/// <summary>Temporary Netscape cookies file for yt-dlp, deleted on dispose.</summary>
public sealed class CookieFile : IDisposable
{
    public string Path { get; }

    CookieFile(string path) => Path = path;

    public static CookieFile? Create(string url, RequestContext ctx)
    {
        var jar = ctx.CookieJar;
        if ((jar is null || jar.Count == 0) && !string.IsNullOrEmpty(ctx.Cookies) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Only a Cookie header is known: scope it to this host.
            jar = ctx.Cookies.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .Select(p => new BrowserCookie { Name = p[0], Value = p[1], Domain = uri.Host, HostOnly = true, Secure = uri.Scheme == "https" })
                .ToList();
        }
        if (jar is null || jar.Count == 0) return null;

        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in jar)
        {
            var domain = c.HostOnly ? c.Domain.TrimStart('.') : (c.Domain.StartsWith('.') ? c.Domain : "." + c.Domain);
            var expiry = c.ExpirationDate is { } e ? ((long)e).ToString(CultureInfo.InvariantCulture) : "0";
            sb.Append(domain).Append('\t').Append(c.HostOnly ? "FALSE" : "TRUE").Append('\t').Append(c.Path).Append('\t')
              .Append(c.Secure ? "TRUE" : "FALSE").Append('\t').Append(expiry).Append('\t').Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        }
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dm-cookies-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, sb.ToString());
        return new CookieFile(path);
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { }
    }
}

/// <summary>
/// Download through yt-dlp (site extractors, DASH). yt-dlp keeps ".part" files, so pausing kills the process
/// tree and resuming continues them. The global limit cannot be shared with an external process; the smaller of
/// the global and per-download limits is passed as <c>--limit-rate</c> and a change restarts the process.
/// </summary>
public sealed class YtDlpTask : ITransfer
{
    const string ProgressPrefix = "DMPROG ";
    const string FilePrefix = "DMFILE:";

    readonly DownloadItem _item;
    readonly DownloadOptions _options;
    readonly Tools _tools;
    readonly Dictionary<string, (long Done, long? Total, bool Estimate)> _streams = [];
    CancellationTokenSource? _cts;
    volatile bool _restart;

    public YtDlpTask(DownloadItem item, DownloadOptions options, Tools tools)
    {
        _item = item;
        _options = options;
        _tools = tools;
    }

    public void Pause() => _cts?.Cancel();

    public void SetSpeedLimit(long bytesPerSecond)
    {
        _item.SpeedLimit = Math.Max(0, bytesPerSecond);
        if (_cts is { IsCancellationRequested: false })
        {
            _restart = true;
            _cts.Cancel();
        }
    }

    public async Task RunAsync()
    {
        _item.Error = null;
        try
        {
            do
            {
                _restart = false;
                _cts = new CancellationTokenSource();
                try
                {
                    await RunOnceAsync(_cts.Token);
                    return;
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    if (!_restart) Set(DownloadStatus.Paused);
                }
            } while (_restart);
        }
        catch (Exception ex)
        {
            _item.Error = ex.Message;
            Log.Warn($"yt-dlp download failed ({Log.Redact(_item.Url)}): {ex.Message}");
            Set(DownloadStatus.Error);
        }
    }

    async Task RunOnceAsync(CancellationToken token)
    {
        try
        {
            await TransferAsync(_item.Request, token);
        }
        catch (InvalidOperationException ex) when (YtDlp.ShouldRetryWithoutCookies(ex.Message, _item.Request))
        {
            Log.Warn($"yt-dlp found no formats with cookies for {Log.Redact(_item.Url)}; retrying without them");
            await TransferAsync(YtDlp.WithoutCookies(_item.Request), token);
        }
    }

    async Task TransferAsync(RequestContext request, CancellationToken token)
    {
        var ytDlp = _tools.YtDlp ?? throw new InvalidOperationException("yt-dlp is not installed (Tools → Install yt-dlp)");
        Directory.CreateDirectory(_item.Folder);
        var stem = Path.GetFileNameWithoutExtension(_item.FileName ?? "%(title)s");
        using var cookies = CookieFile.Create(_item.Url, request);

        var args = new List<string>
        {
            "--newline", "--no-playlist", "--no-warnings", "--color", "never", "--progress", "--quiet",
            "--encoding", "utf-8", // the frozen exe ignores PYTHONIOENCODING; titles and paths must survive the pipe
            "-f", _item.FormatSelector ?? "bv*+ba/b",
            "-N", Math.Clamp(_item.MaxConnections ?? _options.MaxConnections, 1, 16).ToString(CultureInfo.InvariantCulture),
            "-o", Path.Combine(_item.Folder, stem.Replace("%", "%%") + ".%(ext)s"),
            "--print", "after_move:" + FilePrefix + "%(filepath)s",
            "--progress-template", "download:" + ProgressPrefix +
                "%(info.format_id)s %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        };
        if (!(_item.FormatSelector ?? "").StartsWith("ba", StringComparison.Ordinal)) args.AddRange(["--merge-output-format", "mp4"]);
        if (_tools.Ffmpeg is { } ffmpeg) args.AddRange(["--ffmpeg-location", ffmpeg]);
        var limit = new[] { _item.SpeedLimit, _options.GlobalLimiter?.BytesPerSecond ?? 0 }.Where(l => l > 0).DefaultIfEmpty(0).Min();
        if (limit > 0) args.AddRange(["--limit-rate", limit.ToString(CultureInfo.InvariantCulture)]);
        YtDlp.AddJsRuntime(args, _tools);
        YtDlp.AddContext(args, request, cookies);
        args.Add(_item.Url);

        Set(DownloadStatus.Connecting);
        string? finalPath = null;
        var sampler = new SpeedSampler();
        var result = await ProcessRunner.RunAsync(ytDlp, args, token, line =>
        {
            if (line.StartsWith(FilePrefix, StringComparison.Ordinal)) finalPath = line[FilePrefix.Length..].Trim();
            else if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) OnProgress(line[ProgressPrefix.Length..], sampler);
        });
        if (result.ExitCode != 0)
            throw new InvalidOperationException(ProcessRunner.Summarize(result.StderrTail, $"yt-dlp exited with code {result.ExitCode}"));
        if (finalPath is null || !File.Exists(finalPath)) throw new IOException("yt-dlp did not report the finished file");

        lock (_item.Sync)
        {
            _item.FileName = Path.GetFileName(finalPath);
            _item.TotalBytes = new FileInfo(finalPath).Length;
            _item.ReceivedBytes = _item.TotalBytes.Value;
            _item.TotalIsEstimate = false;
        }
        _item.Finished = DateTimeOffset.Now;
        _item.Phase = null;
        Set(DownloadStatus.Completed);
    }

    void OnProgress(string text, SpeedSampler sampler)
    {
        var parts = text.Split(' ');
        if (parts.Length < 4) return;
        static long? N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : null;
        var done = N(parts[1]) ?? 0;
        var total = N(parts[2]);
        var estimate = total is null;
        total ??= N(parts[3]);
        lock (_item.Sync)
        {
            _streams[parts[0]] = (done, total, estimate);
            _item.ReceivedBytes = _streams.Values.Sum(s => s.Done);
            _item.TotalBytes = _streams.Values.All(s => s.Total is not null) ? _streams.Values.Sum(s => s.Total!.Value) : null;
            _item.TotalIsEstimate = _streams.Values.Any(s => s.Estimate);
        }
        var finished = total is { } t && done >= t;
        if (_item.Status != DownloadStatus.Downloading) _item.Status = DownloadStatus.Downloading;
        _item.Phase = finished ? "Processing" : null; // merging runs after the last stream completes
        if (sampler.Sample(_item.ReceivedBytes) is { } speed)
        {
            _item.BytesPerSecond = speed;
            _item.RaiseChanged();
        }
    }

    void Set(DownloadStatus status)
    {
        _item.Status = status;
        if (status != DownloadStatus.Downloading) _item.BytesPerSecond = 0;
        _item.RaiseChanged();
    }

    /// <summary>Smoothed bytes per second, emitted at most twice a second.</summary>
    sealed class SpeedSampler
    {
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan _last;
        long _lastBytes = -1;
        double _speed;

        public double? Sample(long bytes)
        {
            var now = _clock.Elapsed;
            if (_lastBytes < 0) { _lastBytes = bytes; _last = now; return 0; }
            var elapsed = (now - _last).TotalSeconds;
            if (elapsed < 0.5) return null;
            var current = Math.Max(0, bytes - _lastBytes) / elapsed;
            _speed = _speed == 0 ? current : _speed * 0.6 + current * 0.4;
            _lastBytes = bytes;
            _last = now;
            return _speed;
        }
    }
}
