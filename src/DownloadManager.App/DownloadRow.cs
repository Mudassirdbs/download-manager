using System;
using System.ComponentModel;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>Grid row projecting a <see cref="DownloadItem"/>; <see cref="Refresh"/> is called on the UI thread.</summary>
sealed class DownloadRow(DownloadItem item) : INotifyPropertyChanged
{
    public DownloadItem Item { get; } = item;

    public string FileName => Item.FileName ?? FileNames.FromUrl(new System.Uri(Item.Url));
    public string Size => (Item.TotalIsEstimate && Item.TotalBytes is not null ? "~" : "") + Format.Bytes(Item.TotalBytes);
    public string Quality => Item.Quality ?? (Item.Kind == DownloadKind.File ? "" : Item.Kind.ToString());
    public double Progress => Item.TotalBytes is long t and > 0 ? Math.Min(100, Item.ReceivedBytes * 100.0 / t) : Item.Status == DownloadStatus.Completed ? 100 : 0;
    public bool Indeterminate => Item.Status == DownloadStatus.Downloading && (Item.TotalBytes is null || Item.Phase is not null);
    public string ProgressText => Item.TotalBytes is > 0 ? $"{Progress:0.0}%" : Format.Bytes(Item.ReceivedBytes);
    public string Speed => Format.Speed(Item.BytesPerSecond);
    public string TimeLeft => Format.Eta(Item.ReceivedBytes, Item.TotalBytes, Item.BytesPerSecond);
    public string Status
    {
        get
        {
            var text = Item.Status switch
            {
                DownloadStatus.Error => $"Error: {Item.Error}",
                DownloadStatus.Downloading when Item.Phase is not null => Item.Phase,
                DownloadStatus.Connecting when Item.Error is not null => Item.Error,
                DownloadStatus.Downloading when Item.Connections > 1 => $"Downloading · {Item.Connections} connections",
                DownloadStatus.Paused when Item.Resumable == false => "Paused (cannot resume: will restart)",
                var s => s.ToString(),
            };
            return Item.SpeedLimit > 0 && Item.Status != DownloadStatus.Completed ? $"{text} · max {Format.Speed(Item.SpeedLimit)}" : text;
        }
    }
    public string Added => Item.Added.LocalDateTime.ToString("g");
    public string Category => FileNames.CategoryOf(Item.FileName);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
