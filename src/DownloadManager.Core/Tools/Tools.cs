namespace DownloadManager.Core;

/// <summary>
/// Locates the external tools (ffmpeg, yt-dlp): the app's own tools folder first, then the folder next to the
/// executable, then PATH. Results are cached until <see cref="Refresh"/>.
/// </summary>
public sealed class Tools
{
    string? _ffmpeg, _ytDlp;
    bool _scanned;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "tools");

    public string Folder { get; }
    readonly bool _searchPath;

    public Tools(string? folder = null, bool searchPath = true)
    {
        Folder = folder ?? DefaultFolder;
        _searchPath = searchPath;
    }

    public string? Ffmpeg
    {
        get { Scan(); return _ffmpeg; }
    }

    public string? YtDlp
    {
        get { Scan(); return _ytDlp; }
    }

    public void Refresh() => _scanned = false;

    void Scan()
    {
        if (_scanned) return;
        _ffmpeg = Find("ffmpeg.exe");
        _ytDlp = Find("yt-dlp.exe");
        _scanned = true;
    }

    string? Find(string exe)
    {
        var candidates = new List<string> { Folder };
        if (_searchPath)
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var dir in candidates)
        {
            try
            {
                var path = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }
        return null;
    }
}
