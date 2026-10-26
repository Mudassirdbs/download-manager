using System.IO.Compression;
using System.Security.Cryptography;

namespace DownloadManager.Core;

/// <summary>
/// Installs the external tools into <see cref="Tools.Folder"/> from their official release locations, checking
/// each download against the SHA-256 the publisher lists next to it. Runs only when the user asks.
/// </summary>
public static class ToolInstaller
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string YtDlpSumsUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    const string FfmpegSumUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";

    public static async Task InstallYtDlpAsync(Tools tools, IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(tools.Folder);
        progress?.Report("Fetching checksums…");
        var sums = await GetTextAsync(YtDlpSumsUrl, token);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim() == "yt-dlp.exe")?[0]
            ?? throw new InvalidDataException("yt-dlp.exe is not listed in SHA2-256SUMS");

        var temp = Path.Combine(tools.Folder, "yt-dlp.exe.download");
        await DownloadAsync(YtDlpUrl, temp, "yt-dlp", progress, token);
        Verify(temp, expected);
        File.Move(temp, Path.Combine(tools.Folder, "yt-dlp.exe"), overwrite: true);
        tools.Refresh();
        progress?.Report("yt-dlp installed");
    }

    public static async Task InstallFfmpegAsync(Tools tools, IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(tools.Folder);
        progress?.Report("Fetching checksum…");
        var expected = (await GetTextAsync(FfmpegSumUrl, token)).Trim().Split(' ')[0];

        var zip = Path.Combine(tools.Folder, "ffmpeg.zip.download");
        try
        {
            await DownloadAsync(FfmpegUrl, zip, "ffmpeg", progress, token);
            Verify(zip, expected);
            progress?.Report("Extracting ffmpeg…");
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{name} not found in the archive");
                entry.ExtractToFile(Path.Combine(tools.Folder, name), overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
        }
        tools.Refresh();
        progress?.Report("ffmpeg installed");
    }

    static async Task<string> GetTextAsync(string url, CancellationToken token) =>
        await Net.GetStringAsync(new Uri(url), new RequestContext(), token);

    static async Task DownloadAsync(string url, string path, string label, IProgress<string>? progress, CancellationToken token)
    {
        using var response = await Net.Client.SendAsync(Net.NewRequest(new Uri(url), new RequestContext()),
            HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(path);
        var buffer = new byte[256 * 1024];
        long done = 0;
        var lastReport = Environment.TickCount64;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            done += read;
            if (Environment.TickCount64 - lastReport > 250)
            {
                lastReport = Environment.TickCount64;
                progress?.Report(total is > 0 ? $"Downloading {label}… {done * 100 / total:0}%" : $"Downloading {label}… {done / 1048576} MB");
            }
        }
    }

    static void Verify(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            File.Delete(path);
            throw new InvalidDataException("Checksum mismatch; the download was discarded");
        }
    }
}
