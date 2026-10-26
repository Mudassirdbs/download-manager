using System;

namespace DownloadManager.App;

static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b) return "";
        double v = b;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{b} B" : $"{v:0.##} {Units[u]}";
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Eta(long received, long? total, double bytesPerSecond)
    {
        if (total is not { } t || bytesPerSecond <= 0 || received >= t) return "";
        var span = TimeSpan.FromSeconds((t - received) / bytesPerSecond);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s" : $"{span.Seconds}s";
    }
}
