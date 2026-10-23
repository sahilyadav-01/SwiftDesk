using System.Collections.Concurrent;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Security;

namespace RemoteDesk.Signaling;

internal sealed class ManagedSession(SessionSnapshot snapshot)
{
    public object SyncRoot { get; } = new();
    public SessionSnapshot Snapshot { get; set; } = snapshot;
    public string? ControllerTicket { get; set; }
    public string? AgentTicket { get; set; }
    public DateTimeOffset? TicketExpiresAt { get; set; }
}

public sealed class SessionRegistry(
    IDeviceStore devices,
    SessionTokenService tokenService,
    TimeProvider timeProvider,
    RemoteDeskServerOptions options) : ISessionStore
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new(StringComparer.Ordinal);
    private readonly IDeviceStore _devices = devices;
    private readonly SessionTokenService _tokenService = tokenService;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly RemoteDeskServerOptions _options = options;

    public SessionSnapshot Create(CreateSessionRequest request)
    {
        ProtocolValidation.Validate(request);
        if (!_devices.ExistsAsync(request.DeviceId).GetAwaiter().GetResult())
        {
            throw new KeyNotFoundException("Target device was not found.");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var now = _timeProvider.GetUtcNow();
            var snapshot = new SessionSnapshot(
                $"ses_{Guid.NewGuid():N}",
                request.DeviceId,
                request.OperatorName,
                request.RequestedPermissions,
                SessionPermission.None,
                SessionState.Pending,
                now,
                now.AddMinutes(_options.RequestLifetimeMinutes));
            if (_sessions.TryAdd(snapshot.SessionId, new ManagedSession(snapshot)))
            {
                return snapshot;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique session identifier.");
    }

    public IReadOnlyList<PendingSessionRequest> GetPendingForDevice(string deviceId) => _sessions.Values
        .Select(GetFreshSnapshot)
        .Where(snapshot => snapshot.DeviceId == deviceId && snapshot.State == SessionState.Pending)
        .OrderBy(snapshot => snapshot.CreatedAt)
        .Select(snapshot => new PendingSessionRequest(
            snapshot.SessionId,
            snapshot.OperatorName,
            snapshot.RequestedPermissions,
            snapshot.ExpiresAt))
        .ToArray();

    public ApproveSessionResponse Decide(
        string deviceId,
        string sessionId,
        ApproveSessionRequest decision)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException("Session was not found.");
        }

        lock (session.SyncRoot)
        {
            RefreshExpiry(session);
            var snapshot = session.Snapshot;
            if (snapshot.DeviceId != deviceId)
            {
                throw new UnauthorizedAccessException("Session belongs to a different device.");
            }

            if (snapshot.State != SessionState.Pending)
            {
                throw new InvalidOperationException($"Session is already {snapshot.State}.");
            }

            if (!decision.Approved)
            {
                session.Snapshot = snapshot with { State = SessionState.Declined };
                return new ApproveSessionResponse(SessionState.Declined, null, null);
            }

            if (!SessionPermissions.IsSubsetOf(decision.GrantedPermissions, snapshot.RequestedPermissions))
            {
                throw new ArgumentException("Granted permissions must be a non-empty subset of the request.", nameof(decision));
            }

            var ticketLifetime = TimeSpan.FromMinutes(_options.TicketLifetimeMinutes);
            var ticketExpiresAt = _timeProvider.GetUtcNow().Add(ticketLifetime);
            session.ControllerTicket = _tokenService.Issue(
                sessionId,
                deviceId,
                SessionRole.Controller,
                decision.GrantedPermissions,
                ticketLifetime);
            session.AgentTicket = _tokenService.Issue(
                sessionId,
                deviceId,
                SessionRole.Agent,
                decision.GrantedPermissions,
                ticketLifetime);
            session.TicketExpiresAt = ticketExpiresAt;
            session.Snapshot = snapshot with
            {
                GrantedPermissions = decision.GrantedPermissions,
                State = SessionState.Approved,
                ExpiresAt = ticketExpiresAt,
            };

            return new ApproveSessionResponse(SessionState.Approved, session.AgentTicket, ticketExpiresAt);
        }
    }

    public SessionStatusResponse GetStatus(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException("Session was not found.");
        }

        lock (session.SyncRoot)
        {
            RefreshExpiry(session);
            var snapshot = session.Snapshot;
            return new SessionStatusResponse(
                snapshot.SessionId,
                snapshot.State,
                snapshot.GrantedPermissions,
                snapshot.State is SessionState.Approved or SessionState.Active ? session.ControllerTicket : null,
                snapshot.ExpiresAt);
        }
    }

    public TokenValidationResult ValidateTicket(string sessionId, string ticket)
    {
        var result = _tokenService.Validate(ticket, expectedSessionId: sessionId);
        if (!result.IsValid || result.Claims is null)
        {
            return result;
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return TokenValidationResult.Failure("Session was not found.");
        }

        lock (session.SyncRoot)
        {
            RefreshExpiry(session);
            var snapshot = session.Snapshot;
            if (snapshot.State is not (SessionState.Approved or SessionState.Active)
                || snapshot.DeviceId != result.Claims.DeviceId
                || snapshot.GrantedPermissions != result.Claims.Permissions)
            {
                return TokenValidationResult.Failure("Session is no longer authorized.");
            }

            return result;
        }
    }

    public void MarkActive(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.SyncRoot)
        {
            if (session.Snapshot.State == SessionState.Approved)
            {
                session.Snapshot = session.Snapshot with { State = SessionState.Active };
            }
        }
    }

    public void MarkDisconnected(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.SyncRoot)
        {
            if (session.Snapshot.State is SessionState.Approved or SessionState.Active)
            {
                session.Snapshot = session.Snapshot with { State = SessionState.Disconnected };
            }
        }
    }

    private SessionSnapshot GetFreshSnapshot(ManagedSession session)
    {
        lock (session.SyncRoot)
        {
            RefreshExpiry(session);
            return session.Snapshot;
        }
    }

    private void RefreshExpiry(ManagedSession session)
    {
        if (session.Snapshot.State is SessionState.Pending or SessionState.Approved
            && session.Snapshot.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            session.Snapshot = session.Snapshot with { State = SessionState.Expired };
            session.ControllerTicket = null;
            session.AgentTicket = null;
        }
    }
}
