using System.Security.Cryptography;
using RemoteDesk.Core;
using RemoteDesk.PrivilegedHelper;
using RemoteDesk.Security;
using Xunit;

namespace RemoteDesk.Core.Tests;

public sealed class PrivilegedRebootTests
{
    private static readonly byte[] SecretKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public void Engine_ValidSignedRequest_ExecutesReboot()
    {
        var executor = new SimulatedRebootExecutor();
        var engine = new PrivilegedRebootEngine(SecretKey, executor);

        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var reason = "Kernel security patch applied";
        var signature = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_123", timestamp, nonce, reason);

        var request = new PrivilegedRebootRequest("ses_123", reason, timestamp, nonce, signature);
        var response = engine.ProcessRequest(request);

        Assert.True(response.Success);
        Assert.Equal(1, executor.RebootCallCount);
        Assert.Equal("Kernel security patch applied", executor.LastReason);
    }

    [Fact]
    public void Engine_TamperedSignature_IsRejected()
    {
        var executor = new SimulatedRebootExecutor();
        var engine = new PrivilegedRebootEngine(SecretKey, executor);

        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var reason = "Routine maintenance";
        var signature = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_123", timestamp, nonce, reason);

        var tamperedSignature = signature[..^1] + (signature[^1] == 'a' ? 'b' : 'a');
        var request = new PrivilegedRebootRequest("ses_123", reason, timestamp, nonce, tamperedSignature);
        var response = engine.ProcessRequest(request);

        Assert.False(response.Success);
        Assert.Equal("invalid_signature", response.ErrorCode);
        Assert.Equal(0, executor.RebootCallCount);
    }

    [Fact]
    public void Engine_ExpiredTimestamp_IsRejected()
    {
        var executor = new SimulatedRebootExecutor();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var engine = new PrivilegedRebootEngine(SecretKey, executor, clock);

        var oldTimestamp = clock.GetUtcNow().AddMinutes(-2).ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var reason = "Old request";
        var signature = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_123", oldTimestamp, nonce, reason);

        var request = new PrivilegedRebootRequest("ses_123", reason, oldTimestamp, nonce, signature);
        var response = engine.ProcessRequest(request);

        Assert.False(response.Success);
        Assert.Equal("expired_timestamp", response.ErrorCode);
        Assert.Equal(0, executor.RebootCallCount);
    }

    [Fact]
    public void Engine_ReplayedNonce_IsRejected()
    {
        var executor = new SimulatedRebootExecutor();
        var engine = new PrivilegedRebootEngine(SecretKey, executor, cooldown: TimeSpan.Zero);

        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var reason = "First attempt";
        var signature = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_123", timestamp, nonce, reason);

        var request = new PrivilegedRebootRequest("ses_123", reason, timestamp, nonce, signature);
        var first = engine.ProcessRequest(request);
        var replay = engine.ProcessRequest(request);

        Assert.True(first.Success);
        Assert.False(replay.Success);
        Assert.Equal("replay_detected", replay.ErrorCode);
        Assert.Equal(1, executor.RebootCallCount);
    }

    [Fact]
    public void Engine_RateLimiting_CooldownEnforced()
    {
        var executor = new SimulatedRebootExecutor();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var engine = new PrivilegedRebootEngine(SecretKey, executor, clock, cooldown: TimeSpan.FromSeconds(60));

        // First reboot
        var ts1 = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var n1 = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var sig1 = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_1", ts1, n1, "Reboot 1");
        var res1 = engine.ProcessRequest(new PrivilegedRebootRequest("ses_1", "Reboot 1", ts1, n1, sig1));
        Assert.True(res1.Success);

        // Advance 10s (inside 60s cooldown)
        clock.Advance(TimeSpan.FromSeconds(10));
        var ts2 = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var n2 = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var sig2 = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_2", ts2, n2, "Reboot 2");
        var res2 = engine.ProcessRequest(new PrivilegedRebootRequest("ses_2", "Reboot 2", ts2, n2, sig2));

        Assert.False(res2.Success);
        Assert.Equal("rate_limited", res2.ErrorCode);

        // Advance past 60s cooldown
        clock.Advance(TimeSpan.FromSeconds(55));
        var ts3 = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var n3 = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var sig3 = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_3", ts3, n3, "Reboot 3");
        var res3 = engine.ProcessRequest(new PrivilegedRebootRequest("ses_3", "Reboot 3", ts3, n3, sig3));

        Assert.True(res3.Success);
        Assert.Equal(2, executor.RebootCallCount);
    }

    [Theory]
    [InlineData("test; rm -rf /")]
    [InlineData("test && reboot")]
    [InlineData("test | cat /etc/shadow")]
    [InlineData("test `id`")]
    [InlineData("test $(whoami)")]
    [InlineData("test\nnewline")]
    public void Engine_UnsafeReasonCharacters_AreBlocked(string unsafeReason)
    {
        var executor = new SimulatedRebootExecutor();
        var engine = new PrivilegedRebootEngine(SecretKey, executor);

        var timestamp = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(16));
        var signature = PrivilegedRebootEngine.ComputeSignature(SecretKey, "ses_123", timestamp, nonce, unsafeReason);

        var request = new PrivilegedRebootRequest("ses_123", unsafeReason, timestamp, nonce, signature);
        var response = engine.ProcessRequest(request);

        Assert.False(response.Success);
        Assert.Equal("invalid_reason", response.ErrorCode);
        Assert.Equal(0, executor.RebootCallCount);
    }

    [Fact]
    public async Task Ipc_EndToEnd_ClientAndServer_ExecutesReboot()
    {
        var pipeName = $"swiftdesk-test-pipe-{Guid.NewGuid():N}";
        var executor = new SimulatedRebootExecutor();
        var engine = new PrivilegedRebootEngine(SecretKey, executor);
        var server = new PrivilegedRebootIpcServer(pipeName, engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = Task.Run(() => server.RunAsync(cts.Token), cts.Token);
        await Task.Delay(100, cts.Token);

        var client = new PrivilegedRebootClient(SecretKey, pipeName);
        var response = await client.RequestRebootAsync("session_integration", "OS maintenance", cts.Token);

        Assert.True(response.Success);
        Assert.Equal(1, executor.RebootCallCount);
        Assert.Equal("OS maintenance", executor.LastReason);

        cts.Cancel();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
