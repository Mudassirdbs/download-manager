using System;
using System.IO;
using Microsoft.Win32;

namespace DownloadManager.App;

/// <summary>"Start with Windows": a per-user Run entry that starts the app in the tray.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DownloadManager";

    static string Command => $"\"{Path.Combine(AppContext.BaseDirectory, "DownloadManager.exe")}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>After an update or a move, point an existing entry at this copy of the app.</summary>
    public static void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && current != Command) key.SetValue(ValueName, Command);
    }
}
