using System.Text.Json;

namespace DownloadManager.Core;

public sealed class DownloadSettings
{
    public const int MaxConnectionsLimit = 32;

    /// <summary>Connections per download when the server supports ranges.</summary>
    public int MaxConnections { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>Combined limit for all downloads in bytes per second; 0 means unlimited.</summary>
    public long GlobalSpeedLimit { get; set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "settings.json");

    public static DownloadSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(path), Json.Options) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
    }
}
