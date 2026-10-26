using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using DownloadManager.Core;
using Microsoft.Win32;

namespace DownloadManager.App;

/// <summary>
/// Registers dmhost.exe as a native messaging host for the current user. Only this app's own manifest files
/// and registry values are written; running it again rewrites the same values.
/// </summary>
static class NativeHostRegistration
{
    static readonly string[] ChromiumKeys =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\Vivaldi\NativeMessagingHosts",
    ];

    const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts";

    static string ManifestFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "native-host");

    public static void Register()
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "dmhost.exe");
        if (!File.Exists(hostExe)) throw new FileNotFoundException("Native host is missing", hostExe);

        Directory.CreateDirectory(ManifestFolder);
        var chromium = WriteManifest("chromium.json", new
        {
            name = Bridge.NativeHostName,
            description = "DownloadManager browser bridge",
            path = hostExe,
            type = "stdio",
            allowed_origins = Distribution.Current.ChromiumExtensionIds.Select(id => $"chrome-extension://{id}/").ToArray(),
        });
        var firefox = WriteManifest("firefox.json", new
        {
            name = Bridge.NativeHostName,
            description = "DownloadManager browser bridge",
            path = hostExe,
            type = "stdio",
            allowed_extensions = Distribution.Current.FirefoxExtensionIds.ToArray(),
        });

        foreach (var key in ChromiumKeys) SetDefault(key, chromium);
        SetDefault(FirefoxKey, firefox);
    }

    public static void Unregister()
    {
        foreach (var key in ChromiumKeys) Registry.CurrentUser.DeleteSubKeyTree($@"{key}\{Bridge.NativeHostName}", false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{FirefoxKey}\{Bridge.NativeHostName}", false);
        if (Directory.Exists(ManifestFolder)) Directory.Delete(ManifestFolder, recursive: true);
    }

    static string WriteManifest(string name, object manifest)
    {
        var path = Path.Combine(ManifestFolder, name);
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        return path;
    }

    static void SetDefault(string parent, string manifestPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{parent}\{Bridge.NativeHostName}");
        if (key.GetValue(null) as string != manifestPath) key.SetValue(null, manifestPath);
    }
}
