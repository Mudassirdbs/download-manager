using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DownloadManager.Core;
using Forms = System.Windows.Forms;

namespace DownloadManager.App;

public partial class App : Application
{
    Mutex? _instance;
    EventWaitHandle? _showSignal;
    RegisteredWaitHandle? _showWait;
    DownloadService? _service;
    BridgeServer? _bridge;
    Forms.NotifyIcon? _tray;
    MainWindow? _main;
    readonly BridgeConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--unregister"))
        {
            NativeHostRegistration.Unregister();
            Shutdown();
            return;
        }

        var background = e.Args.Contains("--background");
        var user = Environment.UserName;
        _instance = new Mutex(true, $"DownloadManager.Instance.{user}", out var first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $"DownloadManager.Show.{user}");
        if (!first)
        {
            if (!background) _showSignal.Set();
            Shutdown();
            return;
        }

        try { NativeHostRegistration.Register(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Browser integration could not be registered:\n{ex.Message}", "DownloadManager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _service = new DownloadService(DownloadService.DefaultStorePath);
        _service.Load();

        _bridge = new BridgeServer(HandleBridgeAsync);
        _main = new MainWindow(_service, _bridge, NewDownload);
        _tray = CreateTray();
        _bridge.Start();
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, -1, false);

        if (!background) ShowMain();
    }

    Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DownloadManager", null, (_, _) => ShowMain());
        menu.Items.Add("Add URL…", null, (_, _) => NewDownload(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        var tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "DownloadManager",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowMain();
        return tray;
    }

    void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void ExitApp()
    {
        _showWait?.Unregister(null);
        _bridge?.Dispose();
        _service?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _main?.CloseForExit();
        _instance?.ReleaseMutex();
        Shutdown();
    }

    async Task<BridgeMessage?> HandleBridgeAsync(BridgeMessage message)
    {
        switch (message.Type)
        {
            case "hello":
                return new BridgeMessage { Type = "hello", Version = typeof(App).Assembly.GetName().Version?.ToString(), Config = _config };

            case "resolve":
                if (_service!.Tools.YtDlp is not { } ytDlp)
                    return new BridgeMessage { Type = "formats", RequestId = message.RequestId, Error = "yt-dlp is not installed. Open DownloadManager → Tools to install it." };
                try
                {
                    var info = await YtDlp.ResolveAsync(ytDlp, message.PageUrl ?? message.Url ?? "", ContextOf(message), CancellationToken.None);
                    return new BridgeMessage { Type = "formats", RequestId = message.RequestId, Title = info.Title, Formats = info.Choices };
                }
                catch (Exception ex)
                {
                    return new BridgeMessage { Type = "formats", RequestId = message.RequestId, Error = ex.Message };
                }

            case "download" or "link" or "media" or "format":
                if (!Uri.TryCreate(message.Url ?? message.PageUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    return Result(message, false, "Only http and https links are supported");
                // The app owns the download from here; answer now so the browser can drop its copy, then ask the user.
                _ = Dispatcher.BeginInvoke(() => NewDownload(message));
                return Result(message, true);

            case "show":
                _ = Dispatcher.BeginInvoke(ShowMain);
                return null;

            default:
                return null;
        }
    }

    static BridgeMessage Result(BridgeMessage request, bool accepted, string? error = null) =>
        new() { Type = "result", RequestId = request.RequestId, Accepted = accepted, Error = error };

    internal static RequestContext ContextOf(BridgeMessage m) => new()
    {
        Referrer = m.Referrer ?? m.PageUrl,
        Cookies = m.Cookies,
        UserAgent = m.UserAgent,
        Headers = m.Headers,
        CookieJar = m.CookieJar,
    };

    /// <summary>Shows the "Download File Info" dialog, pre-filled from the browser when a message is given.</summary>
    void NewDownload(BridgeMessage? from)
    {
        var dialog = new AddDownloadWindow(from, _service!.Tools);
        if (_main is { IsVisible: true }) dialog.Owner = _main;
        if (dialog.ShowDialog() == true && dialog.Result is { } item) _service!.Add(item, dialog.StartNow);
    }
}
