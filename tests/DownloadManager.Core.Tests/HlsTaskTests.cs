using System.Text.Json;
using Xunit;

namespace DownloadManager.Core.Tests;

/// <summary>
/// End-to-end HLS downloads against streams that ffmpeg generates for each test (the tests that need ffmpeg
/// return early when it is not on PATH).
/// </summary>
public sealed class HlsTaskTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "dm-hls-" + Guid.NewGuid().ToString("N"));
    readonly string _out;
    readonly Tools _withFfmpeg = new();
    readonly Tools _withoutTools;

    public HlsTaskTests()
    {
        _out = Path.Combine(_root, "out");
        Directory.CreateDirectory(_out);
        _withoutTools = new Tools(Path.Combine(_root, "no-tools"), searchPath: false);
    }

    string? Ffmpeg => _withFfmpeg.Ffmpeg;
    string? Ffprobe => Ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe.exe");

    /// <summary>Encodes a 4-second test pattern (and tone) to VOD HLS with one-second segments.</summary>
    string Generate(string name, bool video = true, bool audio = true, bool encrypt = false)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        if (video) args.AddRange(["-f", "lavfi", "-i", "testsrc=duration=4:size=320x240:rate=25"]);
        if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=4"]);
        if (video) args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-g", "25", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        if (encrypt)
        {
            File.WriteAllBytes(Path.Combine(dir, "key.bin"), Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());
            File.WriteAllText(Path.Combine(dir, "keyinfo"), $"key.bin\n{Path.Combine(dir, "key.bin")}\n");
            args.AddRange(["-hls_key_info_file", Path.Combine(dir, "keyinfo")]);
        }
        args.AddRange(["-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod",
            "-hls_segment_filename", Path.Combine(dir, "seg%03d.ts"), Path.Combine(dir, "index.m3u8")]);
        var result = ProcessRunner.RunAsync(Ffmpeg!, args, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, result.StderrTail);
        return dir;
    }

    static void Serve(TestServer server, string dir, string prefix)
    {
        foreach (var file in Directory.GetFiles(dir))
            server.Files[$"/{prefix}/{Path.GetFileName(file)}"] = File.ReadAllBytes(file);
    }

    (double Duration, List<string> Streams) Probe(string file)
    {
        var result = ProcessRunner.RunAsync(Ffprobe!, ["-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json", file],
            CancellationToken.None, captureStdout: true).GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(result.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()!).ToList();
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        return (duration, streams);
    }

    DownloadItem NewItem(string url, string name = "clip.mp4") => new() { Url = url, Kind = DownloadKind.Hls, Folder = _out, FileName = name };

    [Fact]
    public async Task Decrypts_aes128_stream_and_muxes_to_mp4()
    {
        if (Ffmpeg is null) return;
        var dir = Generate("enc", encrypt: true);
        Assert.Contains("METHOD=AES-128", File.ReadAllText(Path.Combine(dir, "index.m3u8")));
        using var server = new TestServer();
        Serve(server, dir, "enc");
        var item = NewItem(server.Url("/enc/index.m3u8"));

        await new HlsTask(item, new DownloadOptions(), _withFfmpeg).RunAsync();

        Assert.True(item.Status == DownloadStatus.Completed, item.Error);
        Assert.Equal("clip.mp4", item.FileName);
        var (duration, streams) = Probe(item.FullPath!);
        Assert.InRange(duration, 3.5, 4.5);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
        Assert.False(Directory.Exists(item.FullPath + ".dmparts"));
    }

    [Fact]
    public async Task Master_playlist_with_separate_audio_is_merged()
    {
        if (Ffmpeg is null) return;
        var video = Generate("video", audio: false);
        var audio = Generate("audio", video: false);
        using var server = new TestServer();
        Serve(server, video, "video");
        Serve(server, audio, "audio");
        server.Files["/master.m3u8"] = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",DEFAULT=YES,URI="audio/index.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240,AUDIO="aud"
            video/index.m3u8
            """u8.ToArray();
        var item = NewItem(server.Url("/master.m3u8"));

        await new HlsTask(item, new DownloadOptions(), _withFfmpeg).RunAsync();

        Assert.True(item.Status == DownloadStatus.Completed, item.Error);
        var (_, streams) = Probe(item.FullPath!);
        Assert.Equal(["video", "audio"], streams);
    }

    [Fact]
    public async Task Without_ffmpeg_keeps_a_ts_file_of_the_joined_segments()
    {
        using var server = new TestServer();
        var segments = Enumerable.Range(0, 5).Select(i => Enumerable.Repeat((byte)(0x40 + i), 10_000 + i).ToArray()).ToList();
        for (var i = 0; i < segments.Count; i++) server.Files[$"/s/seg{i}.ts"] = segments[i];
        server.Files["/s/index.m3u8"] = System.Text.Encoding.ASCII.GetBytes(
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n" + string.Concat(segments.Select((_, i) => $"#EXTINF:2,\nseg{i}.ts\n")) + "#EXT-X-ENDLIST\n");
        var item = NewItem(server.Url("/s/index.m3u8"));

        await new HlsTask(item, new DownloadOptions(), _withoutTools).RunAsync();

        Assert.True(item.Status == DownloadStatus.Completed, item.Error);
        Assert.Equal("clip.ts", item.FileName);
        Assert.Equal(segments.SelectMany(s => s).ToArray(), File.ReadAllBytes(item.FullPath!));
        Assert.False(item.TotalIsEstimate);
    }

    [Fact]
    public async Task Pause_keeps_finished_segments_for_resume()
    {
        using var server = new TestServer { DelayPerChunkMs = 30 };
        for (var i = 0; i < 8; i++) server.Files[$"/p/seg{i}.ts"] = Enumerable.Repeat((byte)i, 40_000).ToArray();
        server.Files["/p/index.m3u8"] = System.Text.Encoding.ASCII.GetBytes(
            "#EXTM3U\n" + string.Concat(Enumerable.Range(0, 8).Select(i => $"#EXTINF:2,\nseg{i}.ts\n")) + "#EXT-X-ENDLIST\n");
        var item = NewItem(server.Url("/p/index.m3u8"));
        item.MaxConnections = 2;
        item.SpeedLimit = 60 * 1024; // slow enough to pause part-way
        var task = new HlsTask(item, new DownloadOptions(), _withoutTools);
        var parts = item.FullPath + ".dmparts";
        var someDone = new TaskCompletionSource();
        item.Changed += _ =>
        {
            if (Directory.Exists(parts) && Directory.GetFiles(parts, "*.seg").Length >= 2) someDone.TrySetResult();
        };

        var run = task.RunAsync();
        await someDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
        task.Pause();
        await run;
        Assert.Equal(DownloadStatus.Paused, item.Status);
        var finished = Directory.GetFiles(parts, "*.seg").Select(Path.GetFileName).ToList();
        Assert.InRange(finished.Count, 2, 7);
        server.Paths.Clear();
        item.SpeedLimit = 0;

        await new HlsTask(item, new DownloadOptions(), _withoutTools).RunAsync();

        Assert.True(item.Status == DownloadStatus.Completed, item.Error);
        Assert.Equal(8 * 40_000, new FileInfo(item.FullPath!).Length);
        foreach (var name in finished)
        {
            var index = int.Parse(name!.Substring(1, 6));
            Assert.DoesNotContain($"/p/seg{index}.ts", server.Paths); // not fetched again
        }
    }

    [Fact]
    public async Task Live_playlists_are_refused()
    {
        using var server = new TestServer();
        server.Files["/live.m3u8"] = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\na.ts\n"u8.ToArray();
        var item = NewItem(server.Url("/live.m3u8"));

        await new HlsTask(item, new DownloadOptions(), _withoutTools).RunAsync();

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("Live", item.Error);
    }

    [Fact]
    public async Task Drm_streams_are_refused()
    {
        using var server = new TestServer();
        server.Files["/drm.m3u8"] = "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:2,\na.ts\n#EXT-X-ENDLIST\n"u8.ToArray();
        var item = NewItem(server.Url("/drm.m3u8"));

        await new HlsTask(item, new DownloadOptions(), _withoutTools).RunAsync();

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("DRM", item.Error);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
