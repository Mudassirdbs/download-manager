using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DownloadManager.Core.Tests;

/// <summary>
/// Minimal HTTP/1.1 file server on a raw socket (no URL ACLs needed) with knobs for the failure modes the
/// engine must survive: dropped connections, ignored Range headers, changed content and slow transfers.
/// </summary>
sealed class TestServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    int _dropsLeft;
    int _active;

    public byte[] Body { get; set; } = [];
    /// <summary>Path (without query) → content; other paths serve <see cref="Body"/>.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Paths { get; } = [];
    public string? DispositionName { get; init; }
    public bool SupportRanges { get; init; } = true;
    /// <summary>Strong ETag sent with every response; If-Range must match it for a 206.</summary>
    public string? ETag { get; set; }
    /// <summary>After <see cref="ArmDrops"/>, abort that many responses after this many body bytes.</summary>
    public int DropAfterBytes { get; init; } = -1;
    public int DelayPerChunkMs { get; init; }
    public List<string?> RangeHeaders { get; } = [];
    public int MaxConcurrent { get; private set; }

    public TestServer()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public string Url(string path = "/file.bin") => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

    public void ArmDrops(int count) => _dropsLeft = count;

    async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        var active = Interlocked.Increment(ref _active);
        lock (RangeHeaders) MaxConcurrent = Math.Max(MaxConcurrent, active);
        try
        {
            using var _ = client;
            await ServeCoreAsync(client);
        }
        catch (IOException)
        {
            // client went away (engine stopped reading a split segment, or paused)
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    async Task ServeCoreAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync();
        if (requestLine is null) return;
        string? range = null, ifRange = null;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
            else if (line.StartsWith("If-Range:", StringComparison.OrdinalIgnoreCase)) ifRange = line[9..].Trim();
        }
        var path = requestLine.Split(" ")[1].Split("?")[0];
        lock (RangeHeaders)
        {
            RangeHeaders.Add(range);
            Paths.Add(path);
        }
        if (requestLine.Contains(" /missing "))
        {
            await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return;
        }

        byte[] body;
        lock (Files) body = Files.TryGetValue(path, out var file) ? file : Body;
        long start = 0, end = body.Length - 1;
        var partial = SupportRanges && range is not null && range.StartsWith("bytes=") && (ifRange is null || ifRange == ETag);
        if (partial)
        {
            var parts = range![6..].Split('-');
            start = long.Parse(parts[0]);
            if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
        }
        var length = end - start + 1;

        var head = new StringBuilder();
        head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: application/octet-stream\r\n");
        head.Append($"Content-Length: {length}\r\n");
        if (SupportRanges) head.Append("Accept-Ranges: bytes\r\n");
        if (ETag is not null) head.Append($"ETag: {ETag}\r\n");
        if (partial) head.Append($"Content-Range: bytes {start}-{end}/{body.Length}\r\n");
        if (DispositionName is not null) head.Append($"Content-Disposition: attachment; filename=\"{DispositionName}\"\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));

        var drop = DropAfterBytes >= 0 && Interlocked.Decrement(ref _dropsLeft) >= 0;
        var limit = drop ? Math.Min(length, DropAfterBytes) : length;
        const int chunk = 16 * 1024;
        for (long sent = 0; sent < limit; sent += chunk)
        {
            var n = (int)Math.Min(chunk, limit - sent);
            await stream.WriteAsync(body.AsMemory((int)(start + sent), n));
            if (DelayPerChunkMs > 0) await Task.Delay(DelayPerChunkMs);
        }
        if (drop) client.Client.LingerState = new LingerOption(true, 0); // abort the connection
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
