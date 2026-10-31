using System.Diagnostics;
using Xunit;

namespace DownloadManager.Core.Tests;

public sealed class DownloadTaskTests : IDisposable
{
    // Small segments so a few hundred KB already exercise splitting.
    static readonly DownloadOptions Segmented = new() { MaxConnections = 8, MinSegmentBytes = 64 * 1024 };
    static readonly DownloadOptions Single = new() { MaxConnections = 1, MinSegmentBytes = 64 * 1024 };

    readonly string _folder = Path.Combine(Path.GetTempPath(), "dm-tests-" + Guid.NewGuid().ToString("N"));

    static byte[] Payload(int size, int seed = 42)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    DownloadItem NewItem(string url, string? name = null) => new() { Url = url, Folder = _folder, FileName = name };

    [Fact]
    public async Task Downloads_file_and_names_it_from_content_disposition()
    {
        var body = Payload(300_000);
        using var server = new TestServer { Body = body, DispositionName = "report.pdf" };
        var item = NewItem(server.Url("/download?id=7"));

        await new DownloadTask(item, Single).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("report.pdf", item.FileName);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.False(File.Exists(item.PartPath));
        Assert.Null(item.Segments);
    }

    [Fact]
    public async Task Splits_into_parallel_connections_and_assembles_correctly()
    {
        var body = Payload(3_000_000);
        using var server = new TestServer { Body = body, DelayPerChunkMs = 2 };
        var item = NewItem(server.Url());

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.InRange(server.MaxConcurrent, 4, 8);
        Assert.Contains(server.RangeHeaders, r => r is not null && !r.StartsWith("bytes=0-"));
    }

    [Fact]
    public async Task Uses_one_connection_when_server_ignores_ranges()
    {
        var body = Payload(1_000_000);
        using var server = new TestServer { Body = body, SupportRanges = false };
        var item = NewItem(server.Url());

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.Equal(1, server.MaxConcurrent);
        Assert.False(item.Resumable);
    }

    [Fact]
    public async Task Recovers_when_a_segment_connection_drops()
    {
        var body = Payload(2_000_000);
        using var server = new TestServer { Body = body, DropAfterBytes = 50_000 };
        server.ArmDrops(3);
        var item = NewItem(server.Url());

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
    }

    [Fact]
    public async Task Restarts_from_zero_when_server_ignores_range()
    {
        var body = Payload(400_000);
        using var server = new TestServer { Body = body, SupportRanges = false, DropAfterBytes = 100_000 };
        server.ArmDrops(1);
        var item = NewItem(server.Url());

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.False(item.Resumable);
    }

    [Fact]
    public async Task Pause_keeps_segments_and_resume_continues_each_range()
    {
        var body = Payload(2_000_000);
        using var server = new TestServer { Body = body, DelayPerChunkMs = 20, ETag = "\"v1\"" };
        var item = NewItem(server.Url(), "movie.mp4");
        item.SpeedLimit = 400 * 1024; // keeps the first run slow enough to pause mid-way
        var task = new DownloadTask(item, Segmented);
        var progressed = new TaskCompletionSource();
        item.Changed += i => { if (i.ReceivedBytes > 300_000) progressed.TrySetResult(); };

        var run = task.RunAsync();
        await progressed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        task.Pause();
        await run;

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.NotNull(item.Segments);
        Assert.True(item.Segments!.Count > 1);
        Assert.Equal(body.Length, new FileInfo(item.PartPath!).Length); // preallocated
        Assert.InRange(item.ReceivedBytes, 1, body.Length - 1);
        Assert.Equal(item.ReceivedBytes, item.Segments.Sum(s => s.Received));
        var resumeFrom = item.Segments.Where(s => s.Remaining > 0).Select(s => s.Position).ToList();
        server.RangeHeaders.Clear();
        item.SpeedLimit = 0;

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.DoesNotContain("bytes=0-", server.RangeHeaders);
        foreach (var position in resumeFrom)
            Assert.Contains(server.RangeHeaders, r => r!.StartsWith($"bytes={position}-"));
    }

    [Fact]
    public async Task Restarts_when_file_changed_on_server_while_paused()
    {
        var original = Payload(1_500_000, seed: 1);
        using var server = new TestServer { Body = original, DelayPerChunkMs = 20, ETag = "\"v1\"" };
        var item = NewItem(server.Url(), "data.bin");
        item.SpeedLimit = 400 * 1024;
        var task = new DownloadTask(item, Segmented);
        var progressed = new TaskCompletionSource();
        item.Changed += i => { if (i.ReceivedBytes > 200_000) progressed.TrySetResult(); };
        var run = task.RunAsync();
        await progressed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        task.Pause();
        await run;
        Assert.Equal(DownloadStatus.Paused, item.Status);
        item.SpeedLimit = 0;

        var updated = Payload(1_200_000, seed: 2);
        server.Body = updated;
        server.ETag = "\"v2\"";

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(updated, File.ReadAllBytes(item.FullPath!));
    }

    [Fact]
    public async Task Per_download_speed_limit_is_enforced()
    {
        var body = Payload(600_000);
        using var server = new TestServer { Body = body };
        var item = NewItem(server.Url());
        item.SpeedLimit = 200 * 1024;

        var clock = Stopwatch.StartNew();
        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        // 600 KB at 200 KB/s is about 2.9 s; allow for the initial burst.
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.2, 8);
    }

    [Fact]
    public async Task Global_limit_is_shared_across_downloads()
    {
        var body = Payload(300_000);
        using var server = new TestServer { Body = body };
        var global = new RateLimiter { BytesPerSecond = 200 * 1024 };
        var options = Segmented with { GlobalLimiter = global };
        var a = NewItem(server.Url("/a.bin"), "a.bin");
        var b = NewItem(server.Url("/b.bin"), "b.bin");

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(new DownloadTask(a, options).RunAsync(), new DownloadTask(b, options).RunAsync());

        Assert.Equal(DownloadStatus.Completed, a.Status);
        Assert.Equal(DownloadStatus.Completed, b.Status);
        // 600 KB combined at 200 KB/s.
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.2, 8);
    }

    [Fact]
    public async Task Repeats_post_downloads_with_their_body_and_headers()
    {
        var body = Payload(200_000);
        using var server = new TestServer { Body = body };
        var item = new DownloadItem
        {
            Url = server.Url("/export"),
            Folder = _folder,
            FileName = "report.csv",
            Request = new RequestContext
            {
                Method = "POST",
                Body = Convert.ToBase64String("id=7&format=csv"u8.ToArray()),
                Headers = new() { ["Authorization"] = "Bearer abc", ["Content-Type"] = "application/x-www-form-urlencoded" },
            },
        };

        await new DownloadTask(item, Single).RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(body, File.ReadAllBytes(item.FullPath!));
        Assert.All(server.Methods, m => Assert.Equal("POST", m));
        Assert.Equal("id=7&format=csv", server.LastBody);
        Assert.Equal("Bearer abc", server.LastAuthorization);
    }

    [Fact]
    public async Task Does_not_overwrite_existing_file()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "setup.exe"), "old");
        using var server = new TestServer { Body = Payload(10_000), DispositionName = "setup.exe" };
        var item = NewItem(server.Url());

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal("setup (1).exe", item.FileName);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_folder, "setup.exe")));
    }

    [Fact]
    public async Task Not_found_fails_without_retrying()
    {
        using var server = new TestServer { Body = Payload(10) };
        var item = NewItem(server.Url("/missing"));

        await new DownloadTask(item, Segmented).RunAsync();

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("404", item.Error);
        Assert.Single(server.RangeHeaders);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
