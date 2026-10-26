using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownloadManager.Core;

/// <summary>
/// Owns the download list, runs at most <see cref="DownloadSettings.MaxConcurrentDownloads"/> transfers,
/// applies the global speed limit and persists the list (including segment progress) and settings.
/// </summary>
public sealed class DownloadService : IDisposable
{
    static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(5);

    readonly object _gate = new();
    readonly object _saveGate = new();
    readonly List<DownloadItem> _items = [];
    readonly Dictionary<Guid, ITransfer> _running = [];
    readonly string _storePath;
    readonly string _settingsPath;
    readonly Timer _saveTimer;
    readonly RateLimiter _globalLimiter = new();
    DateTime _lastSave = DateTime.MinValue;

    public DownloadSettings Settings { get; }
    public Tools Tools { get; }

    public event Action<DownloadItem>? Added;
    public event Action<DownloadItem>? Removed;
    public event Action<DownloadItem>? Changed;

    public DownloadService(string storePath, string? settingsPath = null, Tools? tools = null)
    {
        _storePath = storePath;
        Tools = tools ?? new Tools();
        _settingsPath = settingsPath ?? Path.Combine(Path.GetDirectoryName(storePath)!, "settings.json");
        Settings = DownloadSettings.Load(_settingsPath);
        _globalLimiter.BytesPerSecond = Settings.GlobalSpeedLimit;
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadManager", "downloads.json");

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public void Load()
    {
        if (!File.Exists(_storePath)) return;
        List<DownloadItem>? loaded;
        try { loaded = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(_storePath), Json.Options); }
        catch (JsonException) { File.Copy(_storePath, _storePath + ".corrupt", overwrite: true); return; }
        lock (_gate)
        {
            foreach (var item in loaded ?? [])
            {
                // Transfers do not survive a restart; let the user resume them.
                if (item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Queued)
                    item.Status = DownloadStatus.Paused;
                Track(item);
            }
        }
    }

    public DownloadItem Add(DownloadItem item, bool start)
    {
        item.Status = start ? DownloadStatus.Queued : DownloadStatus.Paused;
        lock (_gate) Track(item);
        Added?.Invoke(item);
        ScheduleSave();
        Pump();
        return item;
    }

    public void Resume(Guid id)
    {
        var item = Find(id);
        if (item is null || item.Status is DownloadStatus.Completed or DownloadStatus.Downloading or DownloadStatus.Connecting) return;
        lock (_gate) if (_running.ContainsKey(id)) return;
        item.Status = DownloadStatus.Queued;
        item.Error = null;
        OnChanged(item);
        Pump();
    }

    public void Pause(Guid id)
    {
        ITransfer? task;
        lock (_gate) _running.TryGetValue(id, out task);
        if (task is not null) task.Pause();
        else if (Find(id) is { Status: DownloadStatus.Queued } queued)
        {
            queued.Status = DownloadStatus.Paused;
            OnChanged(queued);
        }
    }

    public void Remove(Guid id, bool deleteFiles)
    {
        DownloadItem? item;
        ITransfer? task;
        lock (_gate)
        {
            item = _items.Find(i => i.Id == id);
            if (item is null) return;
            _items.Remove(item);
            _running.Remove(id, out task);
        }
        item.Changed -= OnChanged;
        task?.Pause();
        if (item.PartPath is { } part) TryDelete(part);
        if (item.FullPath is { } target && Directory.Exists(target + ".dmparts"))
        {
            try { Directory.Delete(target + ".dmparts", recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (deleteFiles && item.Status == DownloadStatus.Completed && item.FullPath is { } full) TryDelete(full);
        Removed?.Invoke(item);
        ScheduleSave();
        Pump();
    }

    /// <summary>Per-download limit in bytes per second (0 = unlimited); applies immediately to a running transfer.</summary>
    public void SetSpeedLimit(Guid id, long bytesPerSecond)
    {
        var item = Find(id);
        if (item is null) return;
        ITransfer? task;
        lock (_gate) _running.TryGetValue(id, out task);
        if (task is not null) task.SetSpeedLimit(bytesPerSecond);
        else item.SpeedLimit = Math.Max(0, bytesPerSecond);
        OnChanged(item);
    }

    /// <summary>Combined limit for all downloads; applies immediately.</summary>
    public void SetGlobalSpeedLimit(long bytesPerSecond)
    {
        Settings.GlobalSpeedLimit = Math.Max(0, bytesPerSecond);
        _globalLimiter.BytesPerSecond = Settings.GlobalSpeedLimit;
        Settings.Save(_settingsPath);
    }

    /// <summary>Connections per download; used by transfers started after the change.</summary>
    public void SetMaxConnections(int connections)
    {
        Settings.MaxConnections = Math.Clamp(connections, 1, DownloadSettings.MaxConnectionsLimit);
        Settings.Save(_settingsPath);
    }

    DownloadItem? Find(Guid id)
    {
        lock (_gate) return _items.Find(i => i.Id == id);
    }

    void Track(DownloadItem item)
    {
        _items.Add(item);
        item.Changed += OnChanged;
    }

    void OnChanged(DownloadItem item)
    {
        Changed?.Invoke(item);
        // Segment progress is what makes a crash resumable, so save it periodically while downloading.
        if (item.Status is not (DownloadStatus.Downloading or DownloadStatus.Connecting)
            || DateTime.UtcNow - _lastSave > ProgressSaveInterval)
            ScheduleSave();
    }

    void Pump()
    {
        var starts = new List<(DownloadItem, ITransfer)>();
        lock (_gate)
        {
            var options = new DownloadOptions { MaxConnections = Settings.MaxConnections, GlobalLimiter = _globalLimiter };
            foreach (var item in _items)
            {
                if (_running.Count >= Math.Max(1, Settings.MaxConcurrentDownloads)) break;
                if (item.Status != DownloadStatus.Queued || _running.ContainsKey(item.Id)) continue;
                var task = Transfers.Create(item, options, Tools);
                _running[item.Id] = task;
                starts.Add((item, task));
            }
        }
        foreach (var (item, task) in starts) _ = RunAsync(item, task);
    }

    async Task RunAsync(DownloadItem item, ITransfer task)
    {
        try { await Task.Run(task.RunAsync); }
        finally
        {
            lock (_gate) _running.Remove(item.Id);
            ScheduleSave();
            Pump();
        }
    }

    void ScheduleSave() => _saveTimer.Change(500, Timeout.Infinite);

    public void Save()
    {
        var array = new JsonArray();
        foreach (var item in Items)
            lock (item.Sync) // segments change while connections write
                array.Add(JsonSerializer.SerializeToNode(item, Json.Options));
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var temp = _storePath + ".tmp";
        lock (_saveGate) // timer and Dispose may save at once
        {
            File.WriteAllText(temp, array.ToJsonString(Json.Options));
            File.Move(temp, _storePath, overwrite: true);
            _lastSave = DateTime.UtcNow;
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        List<ITransfer> tasks;
        lock (_gate) tasks = _running.Values.ToList();
        foreach (var t in tasks) t.Pause();
        _saveTimer.Dispose();
        Save();
    }
}
