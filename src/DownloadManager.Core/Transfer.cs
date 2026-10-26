namespace DownloadManager.Core;

/// <summary>A running download of any <see cref="DownloadKind"/>. Owns the item's progress while it runs.</summary>
public interface ITransfer
{
    Task RunAsync();
    void Pause();
    void SetSpeedLimit(long bytesPerSecond);
}

public static class Transfers
{
    public static ITransfer Create(DownloadItem item, DownloadOptions options, Tools tools) => item.Kind switch
    {
        DownloadKind.Hls => new HlsTask(item, options, tools),
        DownloadKind.YtDlp => new YtDlpTask(item, options, tools),
        _ => new DownloadTask(item, options),
    };
}
