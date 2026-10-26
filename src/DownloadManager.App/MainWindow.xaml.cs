using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DownloadManager.Core;

namespace DownloadManager.App;

public partial class MainWindow : Window
{
    readonly DownloadService _service;
    readonly BridgeServer _bridge;
    readonly Action<BridgeMessage?> _newDownload;
    readonly ObservableCollection<DownloadRow> _rows = [];
    readonly Dictionary<Guid, DownloadRow> _byId = [];
    bool _exiting;

    internal MainWindow(DownloadService service, BridgeServer bridge, Action<BridgeMessage?> newDownload)
    {
        InitializeComponent();
        _service = service;
        _bridge = bridge;
        _newDownload = newDownload;
        DownloadsGrid.ItemsSource = _rows;

        foreach (var item in service.Items.OrderByDescending(i => i.Added)) AddRow(item, atTop: false);
        service.Added += item => Dispatcher.BeginInvoke(() => AddRow(item, atTop: true));
        service.Removed += item => Dispatcher.BeginInvoke(() => RemoveRow(item));
        service.Changed += item => Dispatcher.BeginInvoke(() => RefreshRow(item));
        bridge.ConnectionsChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        InitLimitControls();
        UpdateStatus();
    }

    sealed record SpeedPreset(string Label, long BytesPerSecond);

    static readonly SpeedPreset[] SpeedPresets =
    [
        new("Unlimited", 0), new("128 KB/s", 128 * 1024), new("256 KB/s", 256 * 1024), new("512 KB/s", 512 * 1024),
        new("1 MB/s", 1024 * 1024), new("2 MB/s", 2 * 1024 * 1024), new("5 MB/s", 5 * 1024 * 1024), new("10 MB/s", 10 * 1024 * 1024),
    ];

    void InitLimitControls()
    {
        var settings = _service.Settings;
        ConnectionsBox.ItemsSource = new[] { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
        ConnectionsBox.SelectedItem = settings.MaxConnections;
        if (ConnectionsBox.SelectedItem is null) ConnectionsBox.SelectedItem = 8;

        var presets = SpeedPresets.ToList();
        if (settings.GlobalSpeedLimit > 0 && presets.All(p => p.BytesPerSecond != settings.GlobalSpeedLimit))
            presets.Add(new SpeedPreset(Format.Speed(settings.GlobalSpeedLimit), settings.GlobalSpeedLimit));
        GlobalSpeedBox.ItemsSource = presets;
        GlobalSpeedBox.SelectedItem = presets.First(p => p.BytesPerSecond == settings.GlobalSpeedLimit);

        foreach (var preset in SpeedPresets)
        {
            var menu = new System.Windows.Controls.MenuItem { Header = preset.Label, Tag = preset.BytesPerSecond };
            menu.Click += (_, _) =>
            {
                foreach (var item in Selected) _service.SetSpeedLimit(item.Id, preset.BytesPerSecond);
            };
            ItemSpeedMenu.Items.Add(menu);
        }
        // Tick the current limit when the selection has a single value.
        ItemSpeedMenu.SubmenuOpened += (_, _) =>
        {
            var limits = Selected.Select(i => i.SpeedLimit).Distinct().ToList();
            foreach (System.Windows.Controls.MenuItem m in ItemSpeedMenu.Items)
                m.IsChecked = limits.Count == 1 && (long)m.Tag == limits[0];
        };
    }

    void OnConnectionsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ConnectionsBox.SelectedItem is int n && n != _service.Settings.MaxConnections) _service.SetMaxConnections(n);
    }

    void OnGlobalSpeedChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GlobalSpeedBox.SelectedItem is SpeedPreset p && p.BytesPerSecond != _service.Settings.GlobalSpeedLimit)
            _service.SetGlobalSpeedLimit(p.BytesPerSecond);
        UpdateStatus();
    }

    void AddRow(DownloadItem item, bool atTop)
    {
        if (_byId.ContainsKey(item.Id)) return;
        var row = new DownloadRow(item);
        _byId[item.Id] = row;
        if (atTop) _rows.Insert(0, row); else _rows.Add(row);
        UpdateStatus();
    }

    void RemoveRow(DownloadItem item)
    {
        if (_byId.Remove(item.Id, out var row)) _rows.Remove(row);
        UpdateStatus();
    }

    void RefreshRow(DownloadItem item)
    {
        if (_byId.TryGetValue(item.Id, out var row)) row.Refresh();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var active = _rows.Where(r => r.Item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting).ToList();
        var speed = active.Sum(r => r.Item.BytesPerSecond);
        var limit = _service.Settings.GlobalSpeedLimit;
        SummaryText.Text = $"{_rows.Count} downloads · {active.Count} active" + (speed > 0 ? $" · {Format.Speed(speed)}" : "")
            + (limit > 0 ? $" · limited to {Format.Speed(limit)}" : "");
        var browsers = _bridge.ConnectionCount;
        BridgeText.Text = browsers == 0 ? "Browser extension: not connected" : $"Browser extension: connected ({browsers})";
    }

    IEnumerable<DownloadItem> Selected => DownloadsGrid.SelectedItems.Cast<DownloadRow>().Select(r => r.Item).ToList();

    void OnAdd(object sender, RoutedEventArgs e) => _newDownload(null);

    void OnResume(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _service.Resume(item.Id);
    }

    void OnPause(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _service.Pause(item.Id);
    }

    void OnResumeAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.ToList()) _service.Resume(row.Item.Id);
    }

    void OnPauseAll(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.ToList()) _service.Pause(row.Item.Id);
    }

    void OnDelete(object sender, RoutedEventArgs e) => Delete(withFiles: false);

    void OnDeleteWithFile(object sender, RoutedEventArgs e)
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (MessageBox.Show(this, $"Delete {items.Count} download(s) and their files from disk?", "DownloadManager",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        Delete(withFiles: true);
    }

    void Delete(bool withFiles)
    {
        foreach (var item in Selected) _service.Remove(item.Id, withFiles);
    }

    void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OnOpenFile(sender, e);

    void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var item = Selected.FirstOrDefault();
        if (item is { Status: DownloadStatus.Completed, FullPath: { } path } && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var item = Selected.FirstOrDefault();
        if (item?.FullPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
        {
            var folder = item?.Folder ?? FileNames.DefaultRoot;
            if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        }
    }

    void OnTools(object sender, RoutedEventArgs e) => new ToolsWindow(_service.Tools) { Owner = this }.ShowDialog();

    void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        var urls = string.Join(Environment.NewLine, Selected.Select(i => i.Url));
        if (urls.Length > 0) Clipboard.SetText(urls);
    }

    /// <summary>Closing the window keeps the app in the tray so browser hand-offs still work.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void CloseForExit()
    {
        _exiting = true;
        Close();
    }
}
