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
        private WebSocket? _controller;
        private WebSocket? _agent;
        private bool _started;

        public bool IsCompleted => _completion.Task.IsCompleted;

        public async Task JoinAsync(
            SessionRole role,
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            var targetRole = role == SessionRole.Controller ? SessionRole.Agent : SessionRole.Controller;

            // Subscribe to backplane for cross-node clustered messages destined for this role
            await using var sub = await backplane.SubscribeAsync(
                sessionId,
                role,
                onMessage: async (payload) =>
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
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
                if (_controller is not null && _agent is not null && !_started)
                {
                    _started = true;
                    _ = RunLocalPairAsync(_controller, _agent);
                }
            }

            // Start single peer pump if running in clustered cross-node mode
            _ = RunPeerPumpAsync(role, targetRole, socket, _relayCancellation.Token);

            using var registration = cancellationToken.Register(() => Abort("Peer request was cancelled."));
            await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Abort(string reason)
        {
            RelayLog.Closing(logger, sessionId, reason);
            _relayCancellation.Cancel();
            _controller?.Abort();
            _agent?.Abort();
            _completion.TrySetResult();
        }

        private async Task RunLocalPairAsync(WebSocket controller, WebSocket agent)
        {
            RelayLog.Paired(logger, sessionId);
            try
            {
                var controllerToAgent = PumpDirectAsync(controller, agent, _relayCancellation.Token);
                var agentToController = PumpDirectAsync(agent, controller, _relayCancellation.Token);
                await Task.WhenAny(controllerToAgent, agentToController);
            }
            catch (OperationCanceledException) when (_relayCancellation.IsCancellationRequested)
            {
                // Expected during normal teardown.
            }
            catch (Exception exception)
            {
                RelayLog.StoppedUnexpectedly(logger, sessionId, exception);
            }
            finally
            {
                _relayCancellation.Cancel();
                await CloseQuietlyAsync(controller);
                await CloseQuietlyAsync(agent);
                _completion.TrySetResult();
                RelayLog.Closed(logger, sessionId);
            }
        }

        private async Task RunPeerPumpAsync(
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

                    // If opposite role is not local, publish through the clustered backplane
                    var isDestinationLocal = false;
                    lock (_syncRoot)
                    {
                        isDestinationLocal = destinationRole == SessionRole.Controller
                            ? _controller is not null
                            : _agent is not null;
                    }

                    if (!isDestinationLocal)
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
                // Expected
            }
            catch
            {
                Abort("Peer pump terminated.");
            }
        }

        private static async Task PumpDirectAsync(
            WebSocket source,
            WebSocket destination,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long currentMessageBytes = 0;

            while (!cancellationToken.IsCancellationRequested
                && source.State == WebSocketState.Open
                && destination.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                currentMessageBytes += result.Count;
                if (currentMessageBytes > MaxMessageBytes)
                {
                    throw new InvalidDataException("Relay message exceeded the 16 MiB limit.");
                }

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cancellationToken);

                if (result.EndOfMessage)
                {
                    currentMessageBytes = 0;
                }
            }
        }

        private static async Task CloseQuietlyAsync(WebSocket? socket)
        {
            if (socket is null)
            {
                return;
            }

            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended.", timeout.Token);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
            finally
            {
                socket.Dispose();
            }
        }

        public void Dispose()
        {
            _relayCancellation.Cancel();
            _relayCancellation.Dispose();
            _controller?.Dispose();
            _agent?.Dispose();
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
