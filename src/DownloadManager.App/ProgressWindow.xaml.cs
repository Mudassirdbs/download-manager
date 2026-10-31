using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>One download's live progress. Hide only closes the window; the download keeps running.</summary>
public partial class ProgressWindow : Window
{
    readonly DownloadService _service;
    readonly DownloadItem _item;
    readonly DownloadRow _row;

    internal ProgressWindow(DownloadService service, DownloadItem item)
    {
        InitializeComponent();
        _service = service;
        _item = item;
        _row = new DownloadRow(item);
        DataContext = _row;
        UrlText.Text = Log.Redact(item.Url);
        UrlText.ToolTip = item.Url;
        Segments.Item = item;
        ShowBox.IsChecked = service.Settings.ShowProgressWindow;
        service.Changed += OnChanged;
        Closed += (_, _) => service.Changed -= OnChanged;
        Refresh();
    }

    void OnChanged(DownloadItem item)
    {
        if (item == _item) Dispatcher.BeginInvoke(Refresh);
    }

    void Refresh()
    {
        _row.Refresh();
        Segments.InvalidateVisual();
        var status = _item.Status;
        var finished = status == DownloadStatus.Completed;
        var total = _item.TotalBytes is > 0 ? $" of {(_item.TotalIsEstimate ? "~" : "")}{Format.Bytes(_item.TotalBytes)}" : "";
        DownloadedText.Text = Format.Bytes(_item.ReceivedBytes) + total;
        Title = finished ? $"Download complete - {_row.FileName}" : $"{_row.ProgressText} {_row.FileName}";

        Segments.Visibility = _item.Segments is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Content = status is DownloadStatus.Paused or DownloadStatus.Error ? "Resume" : "Pause";
        CancelButton.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = FolderButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        HideButton.Content = finished ? "Close" : "Hide";
    }

    void OnPauseResume(object sender, RoutedEventArgs e)
    {
        if (_item.Status is DownloadStatus.Paused or DownloadStatus.Error) _service.Resume(_item.Id);
        else _service.Pause(_item.Id);
    }

    void OnHide(object sender, RoutedEventArgs e) => Close();

    /// <summary>Stops the transfer and closes; the partial file stays, so it can be resumed from the list.</summary>
    void OnCancel(object sender, RoutedEventArgs e)
    {
        _service.Pause(_item.Id);
        Close();
    }

    void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_item.FullPath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Close();
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_item.FullPath is { } path && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        Close();
    }

    void OnShowChanged(object sender, RoutedEventArgs e)
    {
        _service.Settings.ShowProgressWindow = ShowBox.IsChecked == true;
        _service.SaveSettings();
    }
}

/// <summary>Shows which byte ranges have arrived, one stretch per connection.</summary>
public sealed class SegmentBar : FrameworkElement
{
    static readonly Brush Track = Frozen(Color.FromRgb(0xE5, 0xE7, 0xEB));
    static readonly Brush Done = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));

    internal DownloadItem? Item { get; set; }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Track, null, new Rect(RenderSize));
        if (Item?.TotalBytes is not > 0) return;
        var scale = ActualWidth / Item.TotalBytes.Value;
        lock (Item.Sync)
        {
            foreach (var s in Item.Segments?.Where(s => s.Received > 0) ?? [])
                dc.DrawRectangle(Done, null, new Rect(s.Start * scale, 0, Math.Min(s.Received * scale, ActualWidth), ActualHeight));
        }
    }
}
