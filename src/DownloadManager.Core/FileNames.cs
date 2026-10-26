using System.Net.Http.Headers;

namespace DownloadManager.Core;

public static class FileNames
{
    static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase);

    static FileNames()
    {
        void Add(string category, string extensions)
        {
            foreach (var e in extensions.Split(' ')) Categories[e] = category;
        }
        Add("Video", "mp4 m4v mkv webm avi mov wmv flv mpg mpeg 3gp ts");
        Add("Music", "mp3 m4a aac wav flac ogg opus wma");
        Add("Compressed", "zip rar 7z gz tar bz2 xz iso");
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt epub csv");
        Add("Programs", "exe msi msix appx apk dmg");
    }

    /// <summary>IDM-style category folder for a file name; "General" when the extension is unknown.</summary>
    public static string CategoryOf(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return Categories.TryGetValue(ext, out var c) ? c : "General";
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>File name from Content-Disposition (RFC 6266, filename* preferred), else the last URL path segment.</summary>
    public static string FromResponse(Uri url, HttpContentHeaders? headers)
    {
        var cd = headers?.ContentDisposition;
        var name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name)) name = FromUrl(url);
        return Sanitize(name);
    }

    public static string FromUrl(Uri url)
    {
        var last = url.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrEmpty(last) ? "download" : Uri.UnescapeDataString(last);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.');
        if (clean.Length > 200) clean = clean[..200];
        return clean.Length == 0 ? "download" : clean;
    }

    /// <summary>Returns a name that does not collide with an existing file or partial download in the folder.</summary>
    public static string Unique(string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var i = 1; File.Exists(Path.Combine(folder, candidate)) || File.Exists(Path.Combine(folder, candidate + ".dmpart")); i++)
            candidate = $"{stem} ({i}){ext}";
        return candidate;
    }
}
