using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Security;
using RemoteDesk.Signaling;
using Xunit;

namespace RemoteDesk.Signaling.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task DeviceRegistry_EnrollAndAuthenticateAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var devices = new DeviceRegistry(TimeProvider.System);
        var enrolled = await devices.EnrollAsync("TestHost", "Windows", ct);

        Assert.Equal(12, enrolled.DeviceId.Length);
        Assert.All(enrolled.DeviceId, character => Assert.True(char.IsAsciiDigit(character)));
        Assert.True(await devices.ExistsAsync(enrolled.DeviceId, ct));
        Assert.True(await devices.AuthenticateAsync(enrolled.DeviceId, enrolled.DeviceSecret, ct));
        Assert.False(await devices.AuthenticateAsync(enrolled.DeviceId, "wrong-secret", ct));
    }

    [Fact]
    public async Task SessionRegistry_CreateAndDecideAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = TimeProvider.System;
        var devices = new DeviceRegistry(time);
        var enrolled = await devices.EnrollAsync("TestHost", "Windows", ct);

        var tokenService = new SessionTokenService(new string('k', 32), time);
        var options = new RemoteDeskServerOptions
        {
            RequestLifetimeMinutes = 5,
            TicketLifetimeMinutes = 10,
        };

        var sessions = new SessionRegistry(devices, tokenService, time, options);

        var createReq = new CreateSessionRequest(
            enrolled.DeviceId,
            "Alice",
            SessionPermission.ViewScreen | SessionPermission.ControlInput);

        var snapshot = await sessions.CreateAsync(createReq, ct);
        Assert.StartsWith("ses_", snapshot.SessionId);
        Assert.Equal(SessionState.Pending, snapshot.State);

        var pending = await sessions.GetPendingForDeviceAsync(enrolled.DeviceId, ct);
        Assert.Single(pending);
        Assert.Equal(snapshot.SessionId, pending[0].SessionId);

        var decision = await sessions.DecideAsync(
            enrolled.DeviceId,
            snapshot.SessionId,
            new ApproveSessionRequest(true, SessionPermission.ViewScreen),
            ct);

        Assert.Equal(SessionState.Approved, decision.State);
        Assert.NotNull(decision.AgentTicket);

        var status = await sessions.GetStatusAsync(snapshot.SessionId, ct);
        Assert.Equal(SessionState.Approved, status.State);
        Assert.Equal(SessionPermission.ViewScreen, status.GrantedPermissions);
        Assert.NotNull(status.ControllerTicket);

        var recent = await sessions.GetRecentAsync("Alice", 10, ct);
        var recentSession = Assert.Single(recent);
        Assert.Equal(snapshot.SessionId, recentSession.SessionId);
        Assert.Equal(enrolled.DeviceId, recentSession.DeviceId);
        Assert.Equal(SessionState.Approved, recentSession.State);
        Assert.Empty(await sessions.GetRecentAsync("Another operator", 10, ct));
    }

    [Fact]
    public async Task InMemoryAuditStore_RecordsAndQueriesEventsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var audit = new InMemoryAuditStore(TimeProvider.System);
        await audit.RecordEventAsync("DeviceEnrolled", "dev_123", null, null, "Details", ct);
        await audit.RecordEventAsync("SessionCreated", "dev_123", "ses_456", "Bob", "Details", ct);

        var allEvents = await audit.GetEventsAsync(cancellationToken: ct);
        Assert.Equal(2, allEvents.Count);

        var sessionEvents = await audit.GetEventsAsync(sessionId: "ses_456", cancellationToken: ct);
        Assert.Single(sessionEvents);
        Assert.Equal("SessionCreated", sessionEvents[0].EventType);
    }
}
