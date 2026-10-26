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
    Forms.ToolStripMenuItem? _autostartItem;
    readonly BridgeConfig _config = new();
    readonly CancellationTokenSource _exiting = new();

    static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init("app");
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("Unhandled UI error", ev.Exception);
            MessageBox.Show($"Something went wrong:\n{ev.Exception.Message}\n\nDetails were saved to {Log.Folder}.", "DownloadManager",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true; // keep the tray app (and browser integration) alive
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Unhandled error", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("Unobserved task error", ev.Exception);
            ev.SetObserved();
        };

        if (e.Args.Contains("--unregister"))
        {
            // Run by the uninstaller.
            NativeHostRegistration.Unregister();
            Autostart.Set(false);
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

        Log.Info($"DownloadManager {Version} starting from {AppContext.BaseDirectory} on {Environment.OSVersion}");
        try
        {
            NativeHostRegistration.Register();
            Autostart.Repair();
        }
        catch (Exception ex)
        {
            Log.Error("Browser integration could not be registered", ex);
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
        if (!_service.Settings.SetupShown) _ = Dispatcher.BeginInvoke(() => ShowSetup(firstRun: true));
        _ = KeepYtDlpCurrentAsync(_exiting.Token);
    }

    void ShowSetup(bool firstRun)
    {
        var setup = new SetupWindow(_service!.Tools, firstRun);
        if (_main is { IsVisible: true }) setup.Owner = _main;
        setup.ShowDialog();
        _service.Settings.SetupShown = true;
        _service.SaveSettings();
        if (_autostartItem is not null) _autostartItem.Checked = Autostart.IsEnabled;
    }

    /// <summary>
    /// Sites change often and old yt-dlp versions stop working, so a copy we installed is refreshed every
    /// three days in the background. A yt-dlp the user installed elsewhere (PATH) is left alone.
    /// </summary>
    async Task KeepYtDlpCurrentAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            var tools = _service!.Tools;
            var ours = tools.YtDlp is { } path && path.StartsWith(tools.Folder, StringComparison.OrdinalIgnoreCase);
            if (!ours || _service.Settings.YtDlpCheckedAt > DateTimeOffset.Now.AddDays(-3)) return;
            await ToolInstaller.InstallYtDlpAsync(tools, null, token);
            _service.Settings.YtDlpCheckedAt = DateTimeOffset.Now;
            _service.SaveSettings();
            Log.Info("yt-dlp updated");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"yt-dlp update check failed: {ex.Message}");
        }
    }

    Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open DownloadManager", null, (_, _) => ShowMain());
        menu.Items.Add("Add URL…", null, (_, _) => NewDownload(null));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Setup…", null, (_, _) => ShowSetup(firstRun: false));
        _autostartItem = new Forms.ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        _autostartItem.CheckedChanged += (_, _) =>
        {
            try { Autostart.Set(_autostartItem.Checked); }
            catch (Exception ex) { Log.Error("Could not change autostart", ex); }
        };
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/app.ico"))!.Stream;
        var tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = $"DownloadManager {Version}",
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
        Log.Info("Exiting");
        _exiting.Cancel();
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
