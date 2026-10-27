namespace DownloadManager.Core;

/// <summary>
/// Plain-text daily log in %LOCALAPPDATA%\DownloadManager\logs (kept for 14 days) so problems on other
/// people's machines can be diagnosed. URLs are logged without their query strings, which often carry tokens.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static string _source = "app";
    // Only the app and the native host log; libraries and tests that never call Init stay silent.
    static bool _enabled;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "logs");

    public static void Init(string source)
    {
        _source = source;
        _enabled = true;
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var old in new DirectoryInfo(Folder).GetFiles("*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                old.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    /// <summary>Scheme, host and path only.</summary>
    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "(invalid url)";

    static void Write(string level, string message)
    {
        if (!_enabled) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(Path.Combine(Folder, $"{_source}-{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
