using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DownloadManager.Core;

/// <summary>
/// Installs the external tools into <see cref="Tools.Folder"/> from their official release locations, checking
/// each download against the SHA-256 the publisher lists next to it. The installer normally bundles both; this
/// replaces a missing tool and keeps yt-dlp current. It uses the same builds as the installer: the yt-dlp release
/// for this CPU and the LGPL "shared" FFmpeg build from BtbN/FFmpeg-Builds.
/// </summary>
public static class ToolInstaller
{
    const string YtDlpBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    const string FfmpegBase = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    const string FfmpegBranch = "9.0";

    static bool IsArm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    public static async Task InstallYtDlpAsync(Tools tools, IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(tools.Folder);
        var asset = IsArm64 ? "yt-dlp_arm64.exe" : "yt-dlp.exe";
        progress?.Report("Fetching checksums…");
        var expected = ExpectedHash(await GetTextAsync(YtDlpBase + "SHA2-256SUMS", token), asset);

        var temp = Path.Combine(tools.Folder, "yt-dlp.exe.download");
        await DownloadAsync(YtDlpBase + asset, temp, "yt-dlp", progress, token);
        Verify(temp, expected);
        File.Move(temp, Path.Combine(tools.Folder, "yt-dlp.exe"), overwrite: true);
        tools.Refresh();
        progress?.Report("yt-dlp installed");
    }

    public static async Task InstallFfmpegAsync(Tools tools, IProgress<string>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(tools.Folder);
        var asset = $"ffmpeg-n{FfmpegBranch}-latest-{(IsArm64 ? "winarm64" : "win64")}-lgpl-shared-{FfmpegBranch}.zip";
        progress?.Report("Fetching checksums…");
        var expected = ExpectedHash(await GetTextAsync(FfmpegBase + "checksums.sha256", token), asset);

        var zip = Path.Combine(tools.Folder, "ffmpeg.zip.download");
        try
        {
            await DownloadAsync(FfmpegBase + asset, zip, "ffmpeg", progress, token);
            Verify(zip, expected);
            progress?.Report("Extracting ffmpeg…");
            using var archive = ZipFile.OpenRead(zip);
            // ffmpeg.exe and ffprobe.exe need the LGPL libraries (DLLs) next to them; ffplay is not used.
            var files = archive.Entries.Where(e => e.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && e.Name.Length > 0 && !e.Name.Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (!files.Any(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("ffmpeg.exe not found in the archive");
            foreach (var entry in files) entry.ExtractToFile(Path.Combine(tools.Folder, entry.Name), overwrite: true);
            archive.Entries.FirstOrDefault(e => e.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase))
                ?.ExtractToFile(Path.Combine(tools.Folder, "FFmpeg-LICENSE.txt"), overwrite: true);
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
        }
        tools.Refresh();
        progress?.Report("ffmpeg installed");
    }

    /// <summary>The hash for <paramref name="asset"/> in a sha256sum-style list ("hash  name" or "hash *name").</summary>
    static string ExpectedHash(string sums, string asset) =>
        sums.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].Trim().TrimStart('*') == asset)?[0]
        ?? throw new InvalidDataException($"{asset} is not listed in the published checksums");

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
