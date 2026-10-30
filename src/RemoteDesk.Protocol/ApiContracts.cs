using RemoteDesk.Core;

namespace RemoteDesk.Protocol;

public sealed record DeviceEnrollmentRequest(string Name, string Platform);

public sealed record DeviceEnrollmentResponse(string DeviceId, string DeviceSecret);

public sealed record HeartbeatRequest(string AgentVersion, PlatformCapabilityReport Capabilities);

public sealed record CreateSessionRequest(
    string DeviceId,
    string OperatorName,
    SessionPermission RequestedPermissions);

public sealed record CreateSessionResponse(
    string SessionId,
    SessionState State,
    DateTimeOffset ExpiresAt);

public sealed record PendingSessionRequest(
    string SessionId,
    string OperatorName,
    SessionPermission RequestedPermissions,
    DateTimeOffset ExpiresAt);

public sealed record ApproveSessionRequest(
    bool Approved,
    SessionPermission GrantedPermissions);

public sealed record ApproveSessionResponse(
    SessionState State,
    string? AgentTicket,
    DateTimeOffset? TicketExpiresAt);

public sealed record SessionStatusResponse(
    string SessionId,
    SessionState State,
    SessionPermission GrantedPermissions,
    string? ControllerTicket,
    DateTimeOffset ExpiresAt);

public sealed record RecentSessionResponse(
    string SessionId,
    string DeviceId,
    string OperatorName,
    SessionPermission GrantedPermissions,
    SessionState State,
    DateTimeOffset CreatedAt);

public sealed record ErrorResponse(string Code, string Message);

