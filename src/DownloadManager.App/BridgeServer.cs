using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DownloadManager.Core;

namespace DownloadManager.App;

/// <summary>Named-pipe server; each native host instance (one per browser profile) is one connection.</summary>
sealed class BridgeServer : IDisposable
{
    readonly Func<BridgeMessage, Task<BridgeMessage?>> _handle;
    readonly CancellationTokenSource _cts = new();
    readonly List<Connection> _connections = [];

    public BridgeServer(Func<BridgeMessage, Task<BridgeMessage?>> handle) => _handle = handle;

    public int ConnectionCount
    {
        get { lock (_connections) return _connections.Count; }
    }

    public event Action? ConnectionsChanged;

    public void Start() => _ = AcceptLoopAsync(_cts.Token);

    public async Task BroadcastAsync(BridgeMessage message)
    {
        Connection[] targets;
        lock (_connections) targets = _connections.ToArray();
        foreach (var c in targets) await c.SendAsync(message, _cts.Token);
    }

    async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(Bridge.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(token);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }
            _ = ServeAsync(new Connection(server), token);
        }
    }

    async Task ServeAsync(Connection connection, CancellationToken token)
    {
        lock (_connections) _connections.Add(connection);
        ConnectionsChanged?.Invoke();
        try
        {
            while (await Framing.ReadFrameAsync(connection.Stream, token) is { } frame)
            {
                BridgeMessage? message;
                try { message = Framing.Decode(frame); }
                catch (JsonException) { continue; }
                if (message is null) continue;
                // Handle concurrently: a yt-dlp lookup takes seconds and must not hold up download hand-offs.
                _ = RespondAsync(connection, message, token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
        }
        finally
        {
            lock (_connections) _connections.Remove(connection);
            await connection.Stream.DisposeAsync();
            ConnectionsChanged?.Invoke();
        }
    }

    async Task RespondAsync(Connection connection, BridgeMessage message, CancellationToken token)
    {
        BridgeMessage? reply;
        try { reply = await _handle(message); }
        catch (Exception ex) { reply = new BridgeMessage { Type = "result", RequestId = message.RequestId, Accepted = false, Error = ex.Message }; }
        if (reply is not null) await connection.SendAsync(reply, token);
    }

    public void Dispose() => _cts.Cancel();

    sealed class Connection(NamedPipeServerStream stream)
    {
        readonly SemaphoreSlim _write = new(1, 1);
        public NamedPipeServerStream Stream { get; } = stream;

        public async Task SendAsync(BridgeMessage message, CancellationToken token)
        {
            await _write.WaitAsync(token);
            try { await Framing.WriteFrameAsync(Stream, Framing.Encode(message), token); }
            catch (IOException) { }
            finally { _write.Release(); }
        }
    }
}
