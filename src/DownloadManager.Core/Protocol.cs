using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownloadManager.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };
}

/// <summary>
/// One message between the browser extension and the app. The native host forwards these unchanged,
/// so the same shape is used on both hops.
/// </summary>
public sealed class BridgeMessage
{
    /// <summary>
    /// Extension → app: "hello", "download" (a browser download handed over), "media" (user picked a media
    /// item from the video button), "link" (context menu), "resolve" (list formats of a page with yt-dlp),
    /// "format" (download a page with a chosen yt-dlp format), "show". App → extension: "hello", "result",
    /// "formats", "config".
    /// </summary>
    public string Type { get; set; } = "";
    public string? RequestId { get; set; }

    public string? Url { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public List<BrowserCookie>? CookieJar { get; set; }
    public string? UserAgent { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? FileName { get; set; }
    public long? Size { get; set; }
    public string? Mime { get; set; }
    public string? PageUrl { get; set; }
    public string? Title { get; set; }
    /// <summary>Media kind for "media": progressive, hls, dash, audio or subtitle.</summary>
    public string? Kind { get; set; }
    /// <summary>"format": the chosen yt-dlp selector and its label.</summary>
    public string? FormatSelector { get; set; }
    public string? Quality { get; set; }
    /// <summary>"formats" reply: what yt-dlp found on the page.</summary>
    public List<FormatChoice>? Formats { get; set; }

    public string? Browser { get; set; }
    public string? Version { get; set; }
    /// <summary>On the first "hello": false asks the native host not to start the app if it is not running.</summary>
    public bool? Launch { get; set; }

    public bool? Accepted { get; set; }
    public string? Error { get; set; }
    public BridgeConfig? Config { get; set; }
}

/// <summary>Interception rules the app pushes to the extension.</summary>
public sealed class BridgeConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> CatchExtensions { get; set; } =
    [
        "zip", "rar", "7z", "gz", "tar", "iso", "exe", "msi", "apk", "dmg", "pdf", "doc", "docx", "xls", "xlsx",
        "ppt", "pptx", "epub", "mp3", "m4a", "flac", "wav", "mp4", "mkv", "webm", "avi", "mov", "wmv", "torrent",
    ];
    public List<string> ExcludeSites { get; set; } = [];
    public long MinSizeBytes { get; set; }
    public bool ShowVideoButton { get; set; } = true;
}

/// <summary>Length-prefixed (uint32 little-endian) UTF-8 JSON framing — Chrome's native messaging format, reused on the pipe.</summary>
public static class Framing
{
    /// <summary>Chrome rejects host → browser messages over 1 MB; the browser may send up to 64 MB.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, token)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes) throw new InvalidDataException($"Frame length {length} out of range");
        var body = new byte[length];
        if (!await ReadExactAsync(stream, body, token)) throw new EndOfStreamException("Truncated frame");
        return body;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException("Truncated frame");
            offset += read;
        }
        return true;
    }

    public static byte[] Encode(BridgeMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);

    public static BridgeMessage? Decode(byte[] body) => JsonSerializer.Deserialize<BridgeMessage>(body, Json.Options);
}

public static class Bridge
{
    public const string NativeHostName = "com.downloadmanager.host";
    public const string ChromeExtensionId = "fbogbaonaiahnkkinepjjoephghacphe";
    public const string FirefoxExtensionId = "downloadmanager@local";

    /// <summary>Per-user pipe; the server also restricts it to the current user.</summary>
    public static string PipeName => $"DownloadManager.Bridge.{Environment.UserName}";
}
