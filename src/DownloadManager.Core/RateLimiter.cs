using System.Diagnostics;

namespace DownloadManager.Core;

/// <summary>
/// Token bucket shared by any number of connections. Callers take tokens for bytes they already read and,
/// when the bucket goes negative, wait for the debt to refill. The rate can change at any time.
/// </summary>
public sealed class RateLimiter
{
    const long MinBurst = 16 * 1024;

    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _bytesPerSecond;
    double _tokens;
    TimeSpan _last;

    /// <summary>0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                _tokens = 0;
                _last = _clock.Elapsed;
            }
        }
    }

    /// <summary>Read size that keeps throttled transfers smooth (about eight reads per second).</summary>
    public int ChunkFor(int max)
    {
        var rate = BytesPerSecond;
        return rate <= 0 ? max : (int)Math.Clamp(rate / 8, 4096, max);
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var rate = _bytesPerSecond;
            if (rate <= 0) return;
            var now = _clock.Elapsed;
            var burst = Math.Max(MinBurst, rate / 4);
            _tokens = Math.Min(burst, _tokens + (now - _last).TotalSeconds * rate);
            _last = now;
            _tokens -= bytes;
            if (_tokens >= 0) return;
            wait = TimeSpan.FromSeconds(-_tokens / rate);
        }
        await Task.Delay(wait, token);
    }
}
