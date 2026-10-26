using System.Text.Json.Serialization;

namespace DownloadManager.Core;

public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Error,
}

public enum DownloadKind
{
    /// <summary>A single resource fetched with (segmented) HTTP ranges.</summary>
    File,
    /// <summary>An HLS media playlist (<see cref="DownloadItem.Url"/>) plus an optional audio rendition.</summary>
    Hls,
    /// <summary>A page or manifest URL handed to yt-dlp with <see cref="DownloadItem.FormatSelector"/>.</summary>
    YtDlp,
}

/// <summary>A browser cookie with the scope yt-dlp needs to write a Netscape cookies file.</summary>
public sealed record BrowserCookie
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string Domain { get; init; }
    public string Path { get; init; } = "/";
    public bool Secure { get; init; }
    public bool HostOnly { get; init; }
    /// <summary>Unix seconds; null for session cookies.</summary>
    public double? ExpirationDate { get; init; }
}

/// <summary>Request facts captured from the browser that the server may need to serve the file again.</summary>
public sealed record RequestContext
{
    public string? Referrer { get; init; }
    public string? Cookies { get; init; }
    public string? UserAgent { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    /// <summary>Scoped cookies for tools that make their own requests (yt-dlp).</summary>
    public List<BrowserCookie>? CookieJar { get; init; }
}

/// <summary>
/// A byte range [<see cref="Start"/>, <see cref="End"/>) of the target file. <see cref="End"/> is
/// <see cref="long.MaxValue"/> while the total size is unknown. Mutated under <see cref="DownloadItem.Sync"/>.
/// </summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Received { get; set; }

    [JsonIgnore] public long Position => Start + Received;
    [JsonIgnore] public long Remaining => End - Position;
    /// <summary>A connection currently owns this range.</summary>
    [JsonIgnore] public bool Active { get; set; }
}

/// <summary>One download. Mutated only by its <see cref="DownloadTask"/>; observers listen to <see cref="Changed"/>.</summary>
public sealed class DownloadItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Url { get; set; }
    public DownloadKind Kind { get; init; } = DownloadKind.File;
    public RequestContext Request { get; init; } = new();
    /// <summary>Hls: separate audio rendition playlist muxed into the output.</summary>
    public string? AudioUrl { get; init; }
    /// <summary>YtDlp: format selector passed to <c>-f</c>.</summary>
    public string? FormatSelector { get; init; }
    /// <summary>Human-readable quality chosen by the user, for the list.</summary>
    public string? Quality { get; init; }
    /// <summary>The size is extrapolated (stream segments, tool estimates) rather than reported by a server.</summary>
    public bool TotalIsEstimate { get; set; }
    /// <summary>Phase shown while not transferring bytes, e.g. "Merging with ffmpeg".</summary>
    [JsonIgnore] public string? Phase { get; set; }

    /// <summary>Target folder. The file name is decided from the server response when <see cref="FileName"/> is empty.</summary>
    public required string Folder { get; set; }
    public string? FileName { get; set; }

    public long? TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public bool? Resumable { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? Error { get; set; }
    public DateTimeOffset Added { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }

    /// <summary>Byte ranges of the partial file; null before the first response and after completion.</summary>
    public List<Segment>? Segments { get; set; }
    /// <summary>Strong validator from the first response, sent as If-Range when resuming.</summary>
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Per-download speed limit in bytes per second; 0 means unlimited.</summary>
    public long SpeedLimit { get; set; }
    /// <summary>Overrides the global connection count; 1 after a server proved it cannot serve ranges.</summary>
    public int? MaxConnections { get; set; }

    [JsonIgnore] public double BytesPerSecond { get; set; }
    [JsonIgnore] public int Connections { get; set; }

    [JsonIgnore] public string? FullPath => FileName is null ? null : Path.Combine(Folder, FileName);
    [JsonIgnore] public string? PartPath => FullPath is null ? null : FullPath + ".dmpart";

    /// <summary>Guards <see cref="Segments"/> and <see cref="ReceivedBytes"/> while connections write.</summary>
    [JsonIgnore] public object Sync { get; } = new();

    public event Action<DownloadItem>? Changed;
    internal void RaiseChanged() => Changed?.Invoke(this);
}
