using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>First-run guide: extension, video tools, autostart. Also reachable from the tray menu.</summary>
public partial class SetupWindow : Window
{
    readonly Tools _tools;
    readonly CancellationTokenSource _closing = new();

    internal SetupWindow(Tools tools, bool firstRun)
    {
        InitializeComponent();
        _tools = tools;
        Closed += (_, _) => _closing.Cancel();

        var d = Distribution.Current;
        ChromeButton.Visibility = d.ChromeStoreUrl is null ? Visibility.Collapsed : Visibility.Visible;
        EdgeButton.Visibility = d.EdgeStoreUrl is null ? Visibility.Collapsed : Visibility.Visible;
        FirefoxButton.Visibility = d.FirefoxStoreUrl is null ? Visibility.Collapsed : Visibility.Visible;
        // Without published store pages, the manual route is the only one; show it open.
        if (d.ChromeStoreUrl is null && d.EdgeStoreUrl is null && d.FirefoxStoreUrl is null)
        {
            StorePanel.Visibility = Visibility.Collapsed;
            ManualExpander.IsExpanded = true;
        }
        // On by default (interception needs the app running), unless the installer already asked.
        var installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
        AutostartBox.IsChecked = Autostart.IsEnabled || (firstRun && !installed);
        UpdateTools();
    }

    void UpdateTools()
    {
        _tools.Refresh();
        var missing = (_tools.Ffmpeg is null ? 1 : 0) + (_tools.YtDlp is null ? 1 : 0);
        ToolsText.Text = missing == 0 ? "ffmpeg and yt-dlp are ready." : _tools.Ffmpeg is null && _tools.YtDlp is null
            ? "Not installed yet (about 100 MB download)."
            : $"{(_tools.Ffmpeg is null ? "ffmpeg" : "yt-dlp")} is missing.";
        ToolsButton.IsEnabled = missing > 0;
    }

    static void Open(string? url)
    {
        if (url is not null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void OnChrome(object sender, RoutedEventArgs e) => Open(Distribution.Current.ChromeStoreUrl);
    void OnEdge(object sender, RoutedEventArgs e) => Open(Distribution.Current.EdgeStoreUrl);
    void OnFirefox(object sender, RoutedEventArgs e) => Open(Distribution.Current.FirefoxStoreUrl);

    void OnOpenExtension(object sender, RoutedEventArgs e)
    {
        var folder = Distribution.BundledExtensionFolder;
        if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        else MessageBox.Show(this, "The extension folder is not included in this copy of the app.", Title);
    }

    async void OnInstallTools(object sender, RoutedEventArgs e)
    {
        ToolsButton.IsEnabled = false;
        var progress = new Progress<string>(s => ToolsText.Text = s);
        try
        {
            await Task.Run(async () =>
            {
                if (_tools.Ffmpeg is null) await ToolInstaller.InstallFfmpegAsync(_tools, progress, _closing.Token);
                if (_tools.YtDlp is null) await ToolInstaller.InstallYtDlpAsync(_tools, progress, _closing.Token);
            });
            UpdateTools();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Tool installation failed", ex);
            ToolsText.Text = "Failed: " + ex.Message;
            ToolsButton.IsEnabled = true;
        }
    }

    void OnDone(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartBox.IsChecked == true); }
        catch (Exception ex) { Log.Error("Could not change autostart", ex); }
        DialogResult = true;
    }
}
