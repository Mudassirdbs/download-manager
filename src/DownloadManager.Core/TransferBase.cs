using System.Diagnostics;
using System.Net;

namespace DownloadManager.Core;

/// <summary>Raised for HTTP statuses so retry policy can tell server faults from client faults.</summary>
public sealed class HttpStatusException(HttpStatusCode status, string? reason)
    : Exception($"Server returned {(int)status} {reason}")
{
    public HttpStatusCode Status { get; } = status;
    public bool Transient => (int)Status >= 500 || Status == HttpStatusCode.TooManyRequests || Status == HttpStatusCode.RequestTimeout;
}

/// <summary>The partial data cannot be continued (content changed or ranges refused); start from zero.</summary>
sealed class RestartException(string reason, bool forceSingleConnection) : Exception(reason)
{
    public bool ForceSingleConnection { get; } = forceSingleConnection;
}

public sealed record DownloadOptions
{
    public int MaxConnections { get; init; } = 8;
    /// <summary>Segments are never split below this size; also bounds the read size (see the split invariant).</summary>
    public long MinSegmentBytes { get; init; } = 1024 * 1024;
    public RateLimiter? GlobalLimiter { get; init; }
}

/// <summary>
/// Shared lifecycle of in-process transfers: retry with back-off (reset whenever progress was made),
/// pause by cancellation, speed sampling and status changes.
/// </summary>
public abstract class TransferBase : ITransfer
{
    const int MaxRetries = 5;

    protected readonly DownloadItem Item;
    protected readonly DownloadOptions Options;
    protected readonly RateLimiter Limiter = new();
    CancellationTokenSource? _cts;
    int _connections;

    protected TransferBase(DownloadItem item, DownloadOptions? options)
    {
        Item = item;
        Options = options ?? new DownloadOptions();
        Limiter.BytesPerSecond = item.SpeedLimit;
    }

    protected int MaxConnections => Math.Clamp(Item.MaxConnections ?? Options.MaxConnections, 1, DownloadSettings.MaxConnectionsLimit);

    public void Pause() => _cts?.Cancel();

    public void SetSpeedLimit(long bytesPerSecond)
    {
        Item.SpeedLimit = Math.Max(0, bytesPerSecond);
        Limiter.BytesPerSecond = Item.SpeedLimit;
    }

    protected abstract Task TransferAsync(CancellationToken token);

    /// <summary>Discards partial data after a <see cref="RestartException"/>.</summary>
    protected virtual void Reset(bool forceSingleConnection) { }

    public async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Item.Error = null;
        using var reporting = new CancellationTokenSource();
        var reporter = ReportProgressAsync(reporting.Token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var before = Item.ReceivedBytes;
                try
                {
                    await TransferAsync(token);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    Set(DownloadStatus.Paused);
                    return;
                }
                catch (RestartException ex) when (attempt < MaxRetries)
                {
                    Item.Error = $"{ex.Message} — restarting";
                    Reset(ex.ForceSingleConnection);
                }
                catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
                {
                    if (Item.ReceivedBytes > before) attempt = 0; // progress was made; the server is not hopeless
                    Item.Error = $"{ex.Message} — retrying ({attempt + 1}/{MaxRetries})";
                    Set(DownloadStatus.Connecting);
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token); }
                    catch (OperationCanceledException) { Set(DownloadStatus.Paused); return; }
                }
                catch (Exception ex)
                {
                    Item.Error = ex.Message;
                    Set(DownloadStatus.Error);
                    return;
                }
            }
        }
        finally
        {
            Item.Phase = null;
            reporting.Cancel();
            await reporter;
        }
    }

    protected static bool IsTransient(Exception ex) => ex switch
    {
        HttpStatusException h => h.Transient,
        HttpRequestException or IOException or TimeoutException => true,
        _ => false,
    };

    /// <summary>Counts an open connection for the "N connections" display; dispose to release.</summary>
    protected IDisposable CountConnection()
    {
        Interlocked.Increment(ref _connections);
        return new Releaser(() => Interlocked.Decrement(ref _connections));
    }

    /// <summary>Throttles bytes already read through the global and per-download limiters.</summary>
    protected async ValueTask ThrottleAsync(int bytes, CancellationToken token)
    {
        if (Options.GlobalLimiter is { } global) await global.WaitAsync(bytes, token);
        await Limiter.WaitAsync(bytes, token);
    }

    protected int ChunkSize(int max) => Math.Min(Limiter.ChunkFor(Options.GlobalLimiter?.ChunkFor(max) ?? max), max);

    async Task ReportProgressAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        var lastBytes = Item.ReceivedBytes;
        var lastAt = clock.Elapsed;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var bytes = Item.ReceivedBytes;
                var now = clock.Elapsed;
                if (Item.Status == DownloadStatus.Downloading)
                {
                    var current = Math.Max(0, bytes - lastBytes) / (now - lastAt).TotalSeconds;
                    Item.BytesPerSecond = Item.BytesPerSecond == 0 ? current : Item.BytesPerSecond * 0.6 + current * 0.4;
                    Item.Connections = Volatile.Read(ref _connections);
                    Item.RaiseChanged();
                }
                lastBytes = bytes;
                lastAt = now;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected void Set(DownloadStatus status)
    {
        Item.Status = status;
        if (status != DownloadStatus.Downloading)
        {
            Item.BytesPerSecond = 0;
            Item.Connections = 0;
        }
        Item.RaiseChanged();
    }

    protected void SetPhase(string? phase)
    {
        Item.Phase = phase;
        Item.BytesPerSecond = 0;
        Item.RaiseChanged();
    }

    /// <summary>Moves the finished file to <paramref name="fileName"/> in the folder without overwriting, then completes.</summary>
    protected void Complete(string producedPath, string fileName)
    {
        var name = File.Exists(Path.Combine(Item.Folder, fileName)) ? FileNames.Unique(Item.Folder, fileName) : fileName;
        File.Move(producedPath, Path.Combine(Item.Folder, name));
        lock (Item.Sync)
        {
            Item.FileName = name;
            Item.Segments = null;
            Item.TotalBytes = new FileInfo(Path.Combine(Item.Folder, name)).Length;
            Item.ReceivedBytes = Item.TotalBytes.Value;
            Item.TotalIsEstimate = false;
        }
        Item.Finished = DateTimeOffset.Now;
        Item.Error = null;
        Item.Phase = null;
        Set(DownloadStatus.Completed);
    }

    sealed class Releaser(Action release) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) release();
        }
    }
}
