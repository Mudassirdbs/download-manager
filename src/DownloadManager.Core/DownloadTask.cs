using System.Net;
using System.Net.Http.Headers;

namespace DownloadManager.Core;

/// <summary>
/// Segmented transfer of one resource. The first request asks for <c>bytes=0-</c>; when the server answers 206
/// with a total size the ".dmpart" file is preallocated and the largest unfinished segment is split in half for
/// every free connection slot (and again whenever a connection finishes). Without range support the download
/// runs on one connection. Pausing cancels all connections and keeps the partial file and segment table.
/// </summary>
public sealed class DownloadTask : TransferBase
{
    const int BufferSize = 256 * 1024;

    public DownloadTask(DownloadItem item, DownloadOptions? options = null) : base(item, options) { }

    // Split invariant: a split point is at least MinSegmentBytes past a segment's position, so a read
    // of at most this many bytes can never cross into the range handed to another connection.
    int ReadSize => (int)Math.Min(BufferSize, Options.MinSegmentBytes);

    protected override async Task TransferAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Item.Folder);
        HttpResponseMessage? first = null;
        bool canSplit;
        if (Item.Segments is { Count: > 0 } && Item.PartPath is { } part && File.Exists(part))
        {
            canSplit = Item.Resumable == true && Item.TotalBytes is not null && MaxConnections > 1;
        }
        else
        {
            Set(DownloadStatus.Connecting);
            first = await ProbeAsync(token);
            canSplit = first.StatusCode == HttpStatusCode.PartialContent && MaxConnections > 1
                && Item.TotalBytes >= 2 * Options.MinSegmentBytes;
        }

        Set(DownloadStatus.Downloading);
        await RunSegmentsAsync(first, canSplit, token);
        Complete(Item.PartPath!, Item.FileName!);
    }

    /// <summary>First request of a fresh download: learns size, range support, validators and the file name.</summary>
    async Task<HttpResponseMessage> ProbeAsync(CancellationToken token)
    {
        var request = NewRequest();
        request.Headers.Range = new RangeHeaderValue(0, null);
        var response = await Net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Some servers refuse bytes=0- for empty files; ask for the whole thing.
            response.Dispose();
            response = await Net.Client.SendAsync(NewRequest(), HttpCompletionOption.ResponseHeadersRead, token);
        }
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
        }

        var headers = response.Content.Headers;
        var ranged = response.StatusCode == HttpStatusCode.PartialContent;
        if (ranged && headers.ContentRange is not { From: 0, HasLength: true })
        {
            response.Dispose();
            throw new IOException("Server sent an unusable Content-Range");
        }
        var total = ranged ? headers.ContentRange!.Length : headers.ContentLength;

        Item.TotalBytes = total;
        Item.Resumable = ranged || response.Headers.AcceptRanges.Contains("bytes");
        Item.ETag = response.Headers.ETag is { IsWeak: false } etag ? etag.Tag : null; // weak tags are invalid in If-Range
        Item.LastModified = headers.LastModified;
        if (Item.FileName is null)
        {
            var finalUri = response.RequestMessage?.RequestUri ?? new Uri(Item.Url);
            Item.FileName = FileNames.Unique(Item.Folder, FileNames.FromResponse(finalUri, headers));
        }

        await using (var file = new FileStream(Item.PartPath!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            if (ranged && total is { } length) file.SetLength(length); // segments write at their own offsets

        lock (Item.Sync)
        {
            Item.Segments = [new Segment { Start = 0, End = total ?? long.MaxValue }];
            Item.ReceivedBytes = 0;
        }
        return response;
    }

    async Task RunSegmentsAsync(HttpResponseMessage? first, bool canSplit, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = new Dictionary<Task, Segment>();
        var maxConnections = canSplit ? MaxConnections : 1;
        try
        {
            while (true)
            {
                lock (Item.Sync)
                {
                    if (Item.Segments!.All(s => s.Remaining <= 0)) break;
                    while (running.Count < maxConnections && TakeWork(canSplit) is { } segment)
                    {
                        running[RunSegmentAsync(segment, first, cts.Token)] = segment;
                        first = null;
                    }
                }
                if (running.Count == 0) throw new IOException("No connection could be scheduled");

                var done = await Task.WhenAny(running.Keys);
                var finished = running[done];
                running.Remove(done);
                lock (Item.Sync) finished.Active = false;
                try
                {
                    await done;
                }
                catch (Exception ex) when (running.Count > 0 && !token.IsCancellationRequested
                                           && ex is HttpStatusException or HttpRequestException or IOException)
                {
                    // Other connections are healthy, so this is most likely the server limiting connections per
                    // client. Stop growing; the unfinished range goes back to the pool for the next free slot.
                    maxConnections = running.Count;
                }
            }
        }
        finally
        {
            first?.Dispose();
            cts.Cancel();
            try { await Task.WhenAll(running.Keys); }
            catch { /* already reported by the task that failed first, or cancelled */ }
            lock (Item.Sync)
                foreach (var s in Item.Segments ?? []) s.Active = false;
        }
    }

    /// <summary>Called under <see cref="DownloadItem.Sync"/>: an unowned range, else half of the largest active one.</summary>
    Segment? TakeWork(bool allowSplit)
    {
        var segments = Item.Segments!;
        var free = segments.FirstOrDefault(s => !s.Active && s.Remaining > 0);
        if (free is not null)
        {
            free.Active = true;
            return free;
        }
        if (!allowSplit) return null;

        var largest = segments.Where(s => s.Active && s.End != long.MaxValue).MaxBy(s => s.Remaining);
        if (largest is null || largest.Remaining < 2 * Options.MinSegmentBytes) return null;
        var middle = largest.Position + largest.Remaining / 2;
        var split = new Segment { Start = middle, End = largest.End, Active = true };
        largest.End = middle;
        segments.Insert(segments.IndexOf(largest) + 1, split);
        return split;
    }

    async Task RunSegmentAsync(Segment segment, HttpResponseMessage? response, CancellationToken token)
    {
        await Task.Yield(); // started under the item lock; run the rest outside it
        IDisposable? connection = null;
        try
        {
            response ??= await OpenRangeAsync(segment, token);
            connection = CountConnection();

            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var file = new FileStream(Item.PartPath!, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 0, useAsync: true);
            var buffer = new byte[ReadSize];
            while (true)
            {
                long remaining, position;
                lock (Item.Sync)
                {
                    remaining = segment.Remaining;
                    position = segment.Position;
                }
                if (remaining <= 0) break;

                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize(buffer.Length), remaining)), token);
                if (read == 0)
                {
                    if (segment.End != long.MaxValue)
                        throw new IOException($"Connection closed with {remaining} bytes of its range left");
                    lock (Item.Sync) segment.End = segment.Position; // unknown size: end of stream is the end of file
                    break;
                }

                await ThrottleAsync(read, token);
                file.Position = position;
                await file.WriteAsync(buffer.AsMemory(0, read), token);
                lock (Item.Sync)
                {
                    segment.Received += read;
                    Item.ReceivedBytes += read;
                }
            }
        }
        finally
        {
            connection?.Dispose();
            response?.Dispose();
        }
    }

    async Task<HttpResponseMessage> OpenRangeAsync(Segment segment, CancellationToken token)
    {
        long from, to;
        lock (Item.Sync)
        {
            from = segment.Position;
            to = segment.End;
        }
        var request = NewRequest();
        request.Headers.Range = to == long.MaxValue ? new RangeHeaderValue(from, null) : new RangeHeaderValue(from, to - 1);
        if (Item.ETag is { } etag) request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag));
        else if (Item.LastModified is { } modified) request.Headers.IfRange = new RangeConditionHeaderValue(modified);

        var response = await Net.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, response.ReasonPhrase);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                // With a validator, 200 means If-Range failed: the file changed. Without one the server ignores ranges.
                var validated = request.Headers.IfRange is not null;
                throw new RestartException(validated ? "The file changed on the server" : "The server does not support resuming",
                    forceSingleConnection: !validated);
            }
            var range = response.Content.Headers.ContentRange;
            if (range?.From != from || (range.Length is { } length && Item.TotalBytes is { } total && length != total))
                throw new RestartException("The server returned a different file", forceSingleConnection: false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    HttpRequestMessage NewRequest() => Net.NewRequest(new Uri(Item.Url), Item.Request);

    protected override void Reset(bool forceSingleConnection)
    {
        if (Item.PartPath is { } part) File.Delete(part);
        lock (Item.Sync)
        {
            Item.Segments = null;
            Item.ReceivedBytes = 0;
        }
        Item.ETag = null;
        Item.LastModified = null;
        if (forceSingleConnection) Item.MaxConnections = 1;
    }
}
