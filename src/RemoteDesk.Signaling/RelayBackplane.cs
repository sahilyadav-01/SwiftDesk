using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using RemoteDesk.Core;
using RemoteDesk.Security;

namespace RemoteDesk.Signaling;

public interface IRelayBackplane : IAsyncDisposable
{
    ValueTask PublishAsync(
        string sessionId,
        SessionRole destinationRole,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    ValueTask PublishAbortAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default);

    ValueTask<IAsyncDisposable> SubscribeAsync(
        string sessionId,
        SessionRole localRole,
        Func<ReadOnlyMemory<byte>, ValueTask> onMessage,
        Action<string> onAbort,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryRelayBackplane : IRelayBackplane
{
    private sealed record Subscription(
        Func<ReadOnlyMemory<byte>, ValueTask> OnMessage,
        Action<string> OnAbort);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<SessionRole, Subscription>> _subscriptions = new(StringComparer.Ordinal);

    public ValueTask PublishAsync(
        string sessionId,
        SessionRole destinationRole,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscriptions.TryGetValue(sessionId, out var room)
            && room.TryGetValue(destinationRole, out var sub))
        {
            return sub.OnMessage(payload);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAbortAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_subscriptions.TryGetValue(sessionId, out var room))
        {
            foreach (var sub in room.Values)
            {
                sub.OnAbort(reason);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(
        string sessionId,
        SessionRole localRole,
        Func<ReadOnlyMemory<byte>, ValueTask> onMessage,
        Action<string> onAbort,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var room = _subscriptions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<SessionRole, Subscription>());
        var sub = new Subscription(onMessage, onAbort);
        room[localRole] = sub;

        return ValueTask.FromResult<IAsyncDisposable>(new Unsubscriber(() =>
        {
            if (_subscriptions.TryGetValue(sessionId, out var currentRoom))
            {
                currentRoom.TryRemove(localRole, out _);
                if (currentRoom.IsEmpty)
                {
                    _subscriptions.TryRemove(sessionId, out _);
                }
            }

            return ValueTask.CompletedTask;
        }));
    }

    public ValueTask DisposeAsync()
    {
        _subscriptions.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class Unsubscriber(Func<ValueTask> onDispose) : IAsyncDisposable
    {
        private Func<ValueTask>? _onDispose = onDispose;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _onDispose, null) is { } action)
            {
                await action();
            }
        }
    }
}

public sealed class RedisRelayBackplane : IRelayBackplane
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _password;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<SessionRole, (Func<ReadOnlyMemory<byte>, ValueTask> OnMessage, Action<string> OnAbort)>> _subscriptions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly InMemoryRelayBackplane _fallback = new();

    public RedisRelayBackplane(string connectionString)
    {
        // Parse host:port,password=... or host:port
        var parts = connectionString.Split(',', StringSplitOptions.TrimEntries);
        var endpoint = parts[0];
        var endpointParts = endpoint.Split(':');
        _host = endpointParts[0];
        _port = endpointParts.Length > 1 && int.TryParse(endpointParts[1], out var p) ? p : 6379;

        foreach (var part in parts.Skip(1))
        {
            if (part.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
            {
                _password = part["password=".Length..];
            }
        }
    }

    public async ValueTask PublishAsync(
        string sessionId,
        SessionRole destinationRole,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        // Fallback local memory deliver if local subscriber exists
        await _fallback.PublishAsync(sessionId, destinationRole, payload, cancellationToken);

        // Clustered Redis PUBLISH
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await client.ConnectAsync(_host, _port, linked.Token);

            using var stream = client.GetStream();
            if (!string.IsNullOrEmpty(_password))
            {
                await SendCommandAsync(stream, ["AUTH", _password], linked.Token);
            }

            var channel = $"swiftdesk:relay:{sessionId}:{destinationRole}";
            var base64Payload = Base64Url.Encode(payload.Span);
            await SendCommandAsync(stream, ["PUBLISH", channel, base64Payload], linked.Token);
        }
        catch
        {
            // Silently allow local-only delivery if Redis is temporarily unreachable
        }
    }

    public async ValueTask PublishAbortAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await _fallback.PublishAbortAsync(sessionId, reason, cancellationToken);

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await client.ConnectAsync(_host, _port, linked.Token);

            using var stream = client.GetStream();
            if (!string.IsNullOrEmpty(_password))
            {
                await SendCommandAsync(stream, ["AUTH", _password], linked.Token);
            }

            var channel = $"swiftdesk:relay:{sessionId}:control";
            await SendCommandAsync(stream, ["PUBLISH", channel, $"ABORT:{reason}"], linked.Token);
        }
        catch
        {
            // Continue
        }
    }

    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        string sessionId,
        SessionRole localRole,
        Func<ReadOnlyMemory<byte>, ValueTask> onMessage,
        Action<string> onAbort,
        CancellationToken cancellationToken = default)
    {
        // Subscribe to in-memory fallback
        var localSub = await _fallback.SubscribeAsync(sessionId, localRole, onMessage, onAbort, cancellationToken);

        var room = _subscriptions.GetOrAdd(sessionId, _ => new ConcurrentDictionary<SessionRole, (Func<ReadOnlyMemory<byte>, ValueTask>, Action<string>)>());
        room[localRole] = (onMessage, onAbort);

        return new RedisUnsubscriber(async () =>
        {
            await localSub.DisposeAsync();
            if (_subscriptions.TryGetValue(sessionId, out var currentRoom))
            {
                currentRoom.TryRemove(localRole, out _);
                if (currentRoom.IsEmpty)
                {
                    _subscriptions.TryRemove(sessionId, out _);
                }
            }
        });
    }

    private static async Task SendCommandAsync(NetworkStream stream, string[] args, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"*{args.Length}\r\n");
        foreach (var arg in args)
        {
            var bytes = Encoding.UTF8.GetByteCount(arg);
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"${bytes}\r\n{arg}\r\n");
        }

        var payload = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await _fallback.DisposeAsync();
    }

    private sealed class RedisUnsubscriber(Func<ValueTask> onDispose) : IAsyncDisposable
    {
        private Func<ValueTask>? _onDispose = onDispose;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _onDispose, null) is { } action)
            {
                await action();
            }
        }
    }
}
