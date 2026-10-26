using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DownloadManager.Core.Streams;

namespace DownloadManager.Core;

/// <summary>
/// Downloads an HLS video-on-demand stream: the media playlist in <see cref="DownloadItem.Url"/> (a master
/// playlist resolves to its best variant) and an optional audio rendition. Segments download in parallel into
/// a ".dmparts" folder, so pausing keeps every finished segment; AES-128 segments are decrypted. Tracks are
/// joined and remuxed to MP4 with ffmpeg, or kept as MPEG-TS when ffmpeg is missing and no audio track is separate.
/// </summary>
public sealed class HlsTask : TransferBase
{
    const int SegmentRetries = 4;

    readonly Tools _tools;
    readonly ConcurrentDictionary<Uri, Task<byte[]>> _keys = new();

    public HlsTask(DownloadItem item, DownloadOptions? options, Tools tools) : base(item, options) => _tools = tools;

    // Fixed once per run: completion renames the item (e.g. .mp4 → .ts), which must not move the folder.
    string? _workFolder;
    string WorkFolder => _workFolder ??= Item.FullPath + ".dmparts";

    sealed record Track(string Id, HlsMediaPlaylist Playlist);

    protected override async Task TransferAsync(CancellationToken token)
    {
        Set(DownloadStatus.Connecting);
        var (video, audio) = await LoadVideoAsync(new Uri(Item.Url), token);
        var tracks = new List<Track> { new("v", video) };
        if ((Item.AudioUrl is { } chosen ? new Uri(chosen) : audio) is { } audioUri)
            tracks.Add(new Track("a", HlsParser.ParseMedia(await Net.GetStringAsync(audioUri, Item.Request, token), audioUri)));
        foreach (var t in tracks) Validate(t.Playlist);
        if (tracks.Count > 1 && _tools.Ffmpeg is null)
            throw new NotSupportedException("This stream has separate audio; install ffmpeg to combine it");

        Item.FileName ??= FileNames.Unique(Item.Folder, FileNames.Sanitize(FileNames.FromUrl(new Uri(Item.Url))) + ".mp4");
        _workFolder = Item.FullPath + ".dmparts";
        Directory.CreateDirectory(Item.Folder);
        PrepareWorkFolder(tracks);

        var work = tracks.SelectMany(t => Pieces(t)).ToList();
        var pending = work.Where(p => !File.Exists(p.Path)).ToList();
        lock (Item.Sync) Item.ReceivedBytes = work.Where(p => File.Exists(p.Path)).Sum(p => new FileInfo(p.Path).Length);
        Item.Resumable = true;
        UpdateEstimate(work);

        Set(DownloadStatus.Downloading);
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = MaxConnections, CancellationToken = token },
            async (piece, ct) =>
            {
                await FetchWithRetryAsync(piece, ct);
                UpdateEstimate(work);
            });

        SetPhase("Joining segments");
        var joined = tracks.Select(t => Join(t, work.Where(p => p.Track == t.Id).ToList())).ToList();
        token.ThrowIfCancellationRequested();

        var stem = Path.GetFileNameWithoutExtension(Item.FileName);
        string produced, finalName;
        if (_tools.Ffmpeg is { } ffmpeg)
        {
            SetPhase("Merging with ffmpeg");
            produced = Path.Combine(WorkFolder, "output.mp4");
            await Ffmpeg.MuxAsync(ffmpeg, joined[0], joined.Count > 1 ? joined[1] : null, produced, token);
            finalName = stem + ".mp4";
        }
        else
        {
            produced = joined[0];
            finalName = stem + Path.GetExtension(produced); // .ts (or .mp4 for fMP4 streams)
        }
        Complete(produced, finalName);
        try { Directory.Delete(WorkFolder, recursive: true); }
        catch (IOException) { /* the file is complete; a leftover folder is only clutter */ }
    }

    /// <summary>The video media playlist; a master playlist given directly resolves to its best variant and that variant's audio.</summary>
    async Task<(HlsMediaPlaylist Video, Uri? Audio)> LoadVideoAsync(Uri url, CancellationToken token)
    {
        var text = await Net.GetStringAsync(url, Item.Request, token);
        if (!HlsParser.IsMaster(text)) return (HlsParser.ParseMedia(text, url), null);

        var master = HlsParser.ParseMaster(text, url);
        if (master.HasSessionKeyDrm) throw new NotSupportedException("This stream is DRM-protected");
        var variant = master.Variants.FirstOrDefault() ?? throw new InvalidDataException("The playlist lists no streams");
        var media = HlsParser.ParseMedia(await Net.GetStringAsync(variant.Uri, Item.Request, token), variant.Uri);
        return (media, master.AudioFor(variant)?.Uri);
    }

    static void Validate(HlsMediaPlaylist playlist)
    {
        if (!playlist.EndList) throw new NotSupportedException("Live streams are not supported yet");
        if (playlist.Segments.Count == 0) throw new InvalidDataException("The playlist has no segments");
        if (playlist.Segments.Any(s => s.Key is { IsClearKey: false }))
            throw new NotSupportedException("This stream is DRM-protected (SAMPLE-AES or a key system)");
    }

    sealed record Piece(string Track, int Order, Uri Uri, ByteRange? Range, HlsKey? Key, long Sequence, string Path, bool IsInit);

    /// <summary>Init sections (EXT-X-MAP) and media segments of a track, in playback order.</summary>
    IEnumerable<Piece> Pieces(Track track)
    {
        HlsMap? current = null;
        var order = 0;
        var maps = 0;
        foreach (var s in track.Playlist.Segments)
        {
            if (s.Map is not null && s.Map != current)
            {
                current = s.Map;
                yield return new Piece(track.Id, order++, s.Map.Uri, s.Map.Range, null, 0,
                    Path.Combine(WorkFolder, $"{track.Id}-init{maps++}.seg"), IsInit: true);
            }
            yield return new Piece(track.Id, order++, s.Uri, s.Range, s.Key, s.Sequence,
                Path.Combine(WorkFolder, $"{track.Id}{s.Index:D6}.seg"), IsInit: false);
        }
    }

    /// <summary>Keeps finished segments only if the playlist still has the same shape; otherwise starts clean.</summary>
    void PrepareWorkFolder(List<Track> tracks)
    {
        var shape = JsonSerializer.Serialize(tracks.Select(t => new
        {
            t.Id,
            Count = t.Playlist.Segments.Count,
            Duration = Math.Round(t.Playlist.TotalDuration, 1),
        }));
        var marker = Path.Combine(WorkFolder, "playlist.json");
        if (Directory.Exists(WorkFolder) && (!File.Exists(marker) || File.ReadAllText(marker) != shape))
            Directory.Delete(WorkFolder, recursive: true);
        Directory.CreateDirectory(WorkFolder);
        File.WriteAllText(marker, shape);
    }

    async Task FetchWithRetryAsync(Piece piece, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await FetchAsync(piece, token);
                return;
            }
            catch (Exception ex) when (attempt < SegmentRetries && IsTransient(ex) && !token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token);
            }
        }
    }

    async Task FetchAsync(Piece piece, CancellationToken token)
    {
        using var request = Net.NewRequest(piece.Uri, Item.Request);
        if (piece.Range is { } r) request.Headers.Range = new RangeHeaderValue(r.Offset, r.Offset + r.Length - 1);
        using var connection = CountConnection();
        using var response = await Net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        if (piece.Range is not null && response.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException("The server ignored the segment byte range");

        using var data = new MemoryStream();
        long counted = 0;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize(buffer.Length)), token)) > 0)
            {
                await ThrottleAsync(read, token);
                data.Write(buffer, 0, read);
                counted += read;
                lock (Item.Sync) Item.ReceivedBytes += read;
            }
        }
        catch
        {
            lock (Item.Sync) Item.ReceivedBytes -= counted; // the segment is fetched again from the start
            throw;
        }

        var bytes = data.ToArray();
        if (piece.Key is { Uri: { } keyUri } key)
        {
            var aes = Aes.Create();
            aes.Key = await _keys.GetOrAdd(keyUri, u => LoadKeyAsync(u, token));
            bytes = aes.DecryptCbc(bytes, key.Iv ?? HlsParser.DefaultIv(piece.Sequence), PaddingMode.PKCS7);
        }
        var temp = piece.Path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, token);
        File.Move(temp, piece.Path, overwrite: true); // existence marks the piece as done
    }

    async Task<byte[]> LoadKeyAsync(Uri uri, CancellationToken token)
    {
        using var response = await Net.Client.SendAsync(Net.NewRequest(uri, Item.Request), token);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        var key = await response.Content.ReadAsByteArrayAsync(token);
        return key.Length == 16 ? key : throw new InvalidDataException("The stream key is not a 16-byte AES-128 key");
    }

    /// <summary>Concatenates a track's pieces. fMP4 (init + fragments) and MPEG-TS are both valid when concatenated.</summary>
    string Join(Track track, List<Piece> pieces)
    {
        var first = track.Playlist.Segments[0];
        var segmentExt = Path.GetExtension(first.Uri.AbsolutePath).ToLowerInvariant();
        var ext = first.Map is not null || segmentExt is ".m4s" or ".mp4" or ".m4a" ? ".mp4"
            : segmentExt is ".aac" or ".mp3" or ".ac3" or ".ec3" ? segmentExt // packed audio
            : ".ts";
        var path = Path.Combine(WorkFolder, $"{track.Id}-joined{ext}");
        using var output = File.Create(path);
        foreach (var piece in pieces.OrderBy(p => p.Order))
        {
            using var input = File.OpenRead(piece.Path);
            input.CopyTo(output);
        }
        return path;
    }

    void UpdateEstimate(List<Piece> work)
    {
        var done = work.Count(p => File.Exists(p.Path));
        if (done == 0) return;
        lock (Item.Sync)
        {
            Item.TotalBytes = done == work.Count ? Item.ReceivedBytes : (long)(Item.ReceivedBytes / (double)done * work.Count);
            Item.TotalIsEstimate = done < work.Count;
        }
    }

    protected override void Reset(bool forceSingleConnection)
    {
        if (Directory.Exists(WorkFolder)) Directory.Delete(WorkFolder, recursive: true);
        lock (Item.Sync) Item.ReceivedBytes = 0;
    }
}
