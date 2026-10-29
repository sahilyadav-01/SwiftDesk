using System.Collections.Concurrent;
using System.Net.WebSockets;
using RemoteDesk.Core;

namespace RemoteDesk.Signaling;

public sealed class SessionRelayHub(
    ILogger<SessionRelayHub> logger,
    IRelayBackplane? backplane = null)
{
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ILogger<SessionRelayHub> _logger = logger;
    private readonly IRelayBackplane _backplane = backplane ?? new InMemoryRelayBackplane();

    public async Task JoinAsync(
        string sessionId,
        SessionRole role,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var room = _rooms.GetOrAdd(sessionId, id => new RelayRoom(id, _logger, _backplane));
        try
        {
            await room.JoinAsync(role, socket, cancellationToken);
        }
        finally
        {
            if (room.IsCompleted)
            {
                _rooms.TryRemove(new KeyValuePair<string, RelayRoom>(sessionId, room));
                room.Dispose();
            }
        }
    }

    private sealed class RelayRoom(string sessionId, ILogger logger, IRelayBackplane backplane) : IDisposable
    {
        private const int BufferSize = 64 * 1024;
        private const long MaxMessageBytes = 16L * 1024 * 1024;
        private readonly object _syncRoot = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _relayCancellation = new();
        private readonly SemaphoreSlim _controllerSendLock = new(1, 1);
        private readonly SemaphoreSlim _agentSendLock = new(1, 1);
        private WebSocket? _controller;
        private WebSocket? _agent;
        private bool _disposed;

        public bool IsCompleted => _completion.Task.IsCompleted;

        public async Task JoinAsync(
            SessionRole role,
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            var targetRole = role == SessionRole.Controller ? SessionRole.Agent : SessionRole.Controller;
            var sendLock = role == SessionRole.Controller ? _controllerSendLock : _agentSendLock;

            // Subscribe to backplane for cross-node clustered messages destined for this role
            await using var sub = await backplane.SubscribeAsync(
                sessionId,
                role,
                onMessage: async (payload) =>
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await sendLock.WaitAsync(cancellationToken);
                        try
                        {
                            if (socket.State == WebSocketState.Open)
                            {
                                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
                            }
                        }
                        finally
                        {
                            sendLock.Release();
                        }
                    }
                },
                onAbort: (reason) => Abort(reason),
                cancellationToken: cancellationToken);

            lock (_syncRoot)
            {
                ref var slot = ref role == SessionRole.Controller ? ref _controller : ref _agent;
                if (slot is not null)
                {
                    throw new InvalidOperationException($"A {role} is already attached to this session.");
                }

                slot = socket;
                if (_controller is not null && _agent is not null)
                {
                    RelayLog.Paired(logger, sessionId);
                }
            }

            // Exactly ONE pump loop per connected socket.
            var pumpTask = Task.Run(
                () => PumpSocketAsync(role, targetRole, socket, _relayCancellation.Token),
                _relayCancellation.Token);

            using var registration = cancellationToken.Register(() => Abort("Peer request was cancelled."));

            try
            {
                await Task.WhenAny(_completion.Task, pumpTask);
            }
            finally
            {
                Abort("Peer session terminated.");
            }
        }

        public void Abort(string reason)
        {
            lock (_syncRoot)
            {
                if (_completion.Task.IsCompleted)
                {
                    return;
                }

                RelayLog.Closing(logger, sessionId, reason);
                _relayCancellation.Cancel();
                _controller?.Abort();
                _agent?.Abort();
                _completion.TrySetResult();
            }
        }

        private async Task PumpSocketAsync(
            SessionRole sourceRole,
            SessionRole destinationRole,
            WebSocket source,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long currentMessageBytes = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested && source.State == WebSocketState.Open)
                {
                    var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await backplane.PublishAbortAsync(sessionId, "Peer closed connection.", cancellationToken);
                        Abort("Peer closed connection.");
                        return;
                    }

                    currentMessageBytes += result.Count;
                    if (currentMessageBytes > MaxMessageBytes)
                    {
                        throw new InvalidDataException("Relay message exceeded the 16 MiB limit.");
                    }

                    // Forward to local peer if present on this node; otherwise publish to clustered backplane
                    WebSocket? destination;
                    SemaphoreSlim? destinationSendLock;
                    lock (_syncRoot)
                    {
                        if (destinationRole == SessionRole.Controller)
                        {
                            destination = _controller;
                            destinationSendLock = _controllerSendLock;
                        }
                        else
                        {
                            destination = _agent;
                            destinationSendLock = _agentSendLock;
                        }
                    }

                    if (destination is not null && destination.State == WebSocketState.Open && destinationSendLock is not null)
                    {
                        await destinationSendLock.WaitAsync(cancellationToken);
                        try
                        {
                            if (destination.State == WebSocketState.Open)
                            {
                                await destination.SendAsync(
                                    new ArraySegment<byte>(buffer, 0, result.Count),
                                    result.MessageType,
                                    result.EndOfMessage,
                                    cancellationToken);
                            }
                        }
                        finally
                        {
                            destinationSendLock.Release();
                        }
                    }
                    else
                    {
                        var slice = new ReadOnlyMemory<byte>(buffer, 0, result.Count);
                        await backplane.PublishAsync(sessionId, destinationRole, slice, cancellationToken);
                    }

                    if (result.EndOfMessage)
                    {
                        currentMessageBytes = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during normal shutdown
            }
            catch (Exception exception)
            {
                RelayLog.StoppedUnexpectedly(logger, sessionId, exception);
                Abort($"Pump for {sourceRole} failed: {exception.Message}");
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _relayCancellation.Cancel();
                _relayCancellation.Dispose();
                _controllerSendLock.Dispose();
                _agentSendLock.Dispose();
                _controller?.Dispose();
                _agent?.Dispose();
                RelayLog.Closed(logger, sessionId);
            }
        }
    }

    private static class RelayLog
    {
        private static readonly Action<ILogger, string, Exception?> PairedAction =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(1, nameof(Paired)), "Session relay paired for '{SessionId}'.");

        private static readonly Action<ILogger, string, string, Exception?> ClosingAction =
            LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2, nameof(Closing)), "Closing session relay for '{SessionId}': {Reason}");

        private static readonly Action<ILogger, string, Exception?> ClosedAction =
            LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, nameof(Closed)), "Session relay closed for '{SessionId}'.");

        private static readonly Action<ILogger, string, Exception?> StoppedUnexpectedlyAction =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, nameof(StoppedUnexpectedly)), "Session relay stopped unexpectedly for '{SessionId}'.");

        public static void Paired(ILogger logger, string sessionId) => PairedAction(logger, sessionId, null);
        public static void Closing(ILogger logger, string sessionId, string reason) => ClosingAction(logger, sessionId, reason, null);
        public static void Closed(ILogger logger, string sessionId) => ClosedAction(logger, sessionId, null);
        public static void StoppedUnexpectedly(ILogger logger, string sessionId, Exception exception) =>
            StoppedUnexpectedlyAction(logger, sessionId, exception);
    }
}
