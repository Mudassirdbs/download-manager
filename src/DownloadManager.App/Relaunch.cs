using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>
/// Inno Setup runs under Windows' RedirectionGuard, and processes it starts (the "Open DownloadManager" box,
/// silent-install start) inherit it. Under that policy yt-dlp fails with WinError 448 as soon as PATH holds a
/// junction or symlink (nvm, scoop and similar tools add them). Such an instance restarts itself through
/// Explorer, which starts it as an ordinary process; arguments travel in a short-lived file because Explorer
/// cannot pass them.
/// </summary>
static class Relaunch
{
    const int ProcessRedirectionTrustPolicy = 16;
    static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(30);

    static string HandoffFile => Path.Combine(Path.GetDirectoryName(DownloadService.DefaultStorePath)!, "relaunch-args.txt");

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint flags, nint size);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    public static bool RedirectionGuardEnforced()
    {
        try
        {
            return GetProcessMitigationPolicy(GetCurrentProcess(), ProcessRedirectionTrustPolicy, out var flags, sizeof(uint))
                && (flags & 1) != 0; // EnforceRedirectionTrust
        }
        catch (EntryPointNotFoundException)
        {
            return false; // Windows versions without the policy
        }
    }

    /// <summary>Starts a fresh copy through Explorer; false when that was already tried moments ago (no loop).</summary>
    public static bool TryViaExplorer(string[] args)
    {
        if (File.Exists(HandoffFile) && DateTime.Now - File.GetLastWriteTime(HandoffFile) < HandoffLifetime)
        {
            Log.Warn("Still running under RedirectionGuard after relaunch; continuing (yt-dlp may fail on PATH junctions)");
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(HandoffFile)!);
        File.WriteAllLines(HandoffFile, args);
        var exe = Path.Combine(AppContext.BaseDirectory, "DownloadManager.exe");
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{exe}\"")
        {
            UseShellExecute = false,
        });
        Log.Info("Started under RedirectionGuard (by the installer); restarting through Explorer");
        return true;
    }

    /// <summary>The arguments handed over by <see cref="TryViaExplorer"/>, if this is that relaunch.</summary>
    public static string[]? TakeHandoff()
    {
        try
        {
            if (!File.Exists(HandoffFile)) return null;
            var fresh = DateTime.Now - File.GetLastWriteTime(HandoffFile) < HandoffLifetime;
            var args = File.ReadAllLines(HandoffFile);
            if (!RedirectionGuardEnforced()) File.Delete(HandoffFile); // keep it as the loop guard otherwise
            return fresh ? args : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
