using System.Collections.Concurrent;
using System.Net.WebSockets;
using RemoteDesk.Core;

namespace RemoteDesk.Signaling;

public sealed class SessionRelayHub(ILogger<SessionRelayHub> logger)
{
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.Ordinal);
    private readonly ILogger<SessionRelayHub> _logger = logger;

    public async Task JoinAsync(
        string sessionId,
        SessionRole role,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var room = _rooms.GetOrAdd(sessionId, id => new RelayRoom(id, _logger));
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

    private sealed class RelayRoom(string sessionId, ILogger logger) : IDisposable
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
                    _ = RunPairAsync(_controller, _agent);
                }
            }

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

        private async Task RunPairAsync(WebSocket controller, WebSocket agent)
        {
            RelayLog.Paired(logger, sessionId);
            try
            {
                var controllerToAgent = PumpAsync(controller, agent, _relayCancellation.Token);
                var agentToController = PumpAsync(agent, controller, _relayCancellation.Token);
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

        private static async Task PumpAsync(
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

        private static async Task CloseQuietlyAsync(WebSocket socket)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session ended",
                        CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
                socket.Abort();
            }
        }

        public void Dispose()
        {
            _relayCancellation.Dispose();
        }
    }
}

internal static partial class RelayLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Closing relay {SessionId}: {Reason}")]
    public static partial void Closing(ILogger logger, string sessionId, string reason);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Relay {SessionId} paired")]
    public static partial void Paired(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Relay {SessionId} stopped unexpectedly")]
    public static partial void StoppedUnexpectedly(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Relay {SessionId} closed")]
    public static partial void Closed(ILogger logger, string sessionId);
}
