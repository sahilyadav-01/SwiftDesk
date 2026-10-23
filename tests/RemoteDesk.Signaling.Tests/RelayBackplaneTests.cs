using System.Text;
using RemoteDesk.Core;
using RemoteDesk.Signaling;
using Xunit;

namespace RemoteDesk.Signaling.Tests;

public sealed class RelayBackplaneTests
{
    [Fact]
    public async Task InMemoryRelayBackplane_DeliversMessageToTargetRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backplane = new InMemoryRelayBackplane();

        var messageReceived = new TaskCompletionSource<string>();
        await using var sub = await backplane.SubscribeAsync(
            "session_alpha",
            SessionRole.Agent,
            onMessage: payload =>
            {
                var text = Encoding.UTF8.GetString(payload.Span);
                messageReceived.TrySetResult(text);
                return ValueTask.CompletedTask;
            },
            onAbort: _ => { },
            cancellationToken: ct);

        var data = Encoding.UTF8.GetBytes("Hello from Controller over clustered backplane!");
        await backplane.PublishAsync("session_alpha", SessionRole.Agent, data, ct);

        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        Assert.Equal("Hello from Controller over clustered backplane!", received);
    }

    [Fact]
    public async Task InMemoryRelayBackplane_AbortPropagatesToAllPeers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backplane = new InMemoryRelayBackplane();

        var abortTcs = new TaskCompletionSource<string>();
        await using var sub = await backplane.SubscribeAsync(
            "session_beta",
            SessionRole.Controller,
            onMessage: _ => ValueTask.CompletedTask,
            onAbort: reason => abortTcs.TrySetResult(reason),
            cancellationToken: ct);

        await backplane.PublishAbortAsync("session_beta", "Controller closed window.", ct);

        var reason = await abortTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        Assert.Equal("Controller closed window.", reason);
    }

    [Fact]
    public async Task InMemoryRelayBackplane_UnsubscribedPeer_DoesNotReceiveSubsequentMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backplane = new InMemoryRelayBackplane();

        var callCount = 0;
        var sub = await backplane.SubscribeAsync(
            "session_gamma",
            SessionRole.Agent,
            onMessage: _ =>
            {
                callCount++;
                return ValueTask.CompletedTask;
            },
            onAbort: _ => { },
            cancellationToken: ct);

        await backplane.PublishAsync("session_gamma", SessionRole.Agent, new byte[] { 1 }, ct);
        Assert.Equal(1, callCount);

        await sub.DisposeAsync();

        await backplane.PublishAsync("session_gamma", SessionRole.Agent, new byte[] { 2 }, ct);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RedisRelayBackplane_FallsBackGracefully_WhenNoRedisDaemon()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var backplane = new RedisRelayBackplane("127.0.0.1:54321"); // Unused port

        var messageReceived = new TaskCompletionSource<string>();
        await using var sub = await backplane.SubscribeAsync(
            "session_delta",
            SessionRole.Agent,
            onMessage: payload =>
            {
                var text = Encoding.UTF8.GetString(payload.Span);
                messageReceived.TrySetResult(text);
                return ValueTask.CompletedTask;
            },
            onAbort: _ => { },
            cancellationToken: ct);

        var data = Encoding.UTF8.GetBytes("Fallback message delivered via local memory channel.");
        await backplane.PublishAsync("session_delta", SessionRole.Agent, data, ct);

        var received = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        Assert.Equal("Fallback message delivered via local memory channel.", received);
    }
}
