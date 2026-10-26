using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DownloadManager.Core;

namespace DownloadManager.App;

public partial class ToolsWindow : Window
{
    readonly Tools _tools;
    readonly CancellationTokenSource _closing = new();

    internal ToolsWindow(Tools tools)
    {
        InitializeComponent();
        _tools = tools;
        Closed += (_, _) => _closing.Cancel();
        Refresh();
    }

    void Refresh()
    {
        _tools.Refresh();
        FfmpegText.Text = _tools.Ffmpeg ?? "Not found";
        FfmpegText.ToolTip = _tools.Ffmpeg;
        FfmpegButton.Content = _tools.Ffmpeg is null ? "Install" : "Reinstall";
        YtDlpText.Text = _tools.YtDlp ?? "Not found";
        YtDlpText.ToolTip = _tools.YtDlp;
        YtDlpButton.Content = _tools.YtDlp is null ? "Install" : "Update";
    }

    async void OnInstallFfmpeg(object sender, RoutedEventArgs e) =>
        await RunAsync(p => ToolInstaller.InstallFfmpegAsync(_tools, p, _closing.Token));

    async void OnInstallYtDlp(object sender, RoutedEventArgs e) =>
        await RunAsync(p => ToolInstaller.InstallYtDlpAsync(_tools, p, _closing.Token));

    async Task RunAsync(Func<IProgress<string>, Task> install)
    {
        FfmpegButton.IsEnabled = YtDlpButton.IsEnabled = false;
        var progress = new Progress<string>(s => StatusText.Text = s);
        try
        {
            await Task.Run(() => install(progress));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            if (!_closing.IsCancellationRequested)
            {
                FfmpegButton.IsEnabled = YtDlpButton.IsEnabled = true;
                Refresh();
            }
        }
    }
}
