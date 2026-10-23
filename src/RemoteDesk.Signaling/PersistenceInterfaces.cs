using RemoteDesk.Core;
using RemoteDesk.Protocol;

namespace RemoteDesk.Signaling;

public sealed record AuditEvent(
    long Id,
    string EventType,
    string? DeviceId,
    string? SessionId,
    string? OperatorName,
    string? DetailsJson,
    DateTimeOffset CreatedAt);

public interface IDeviceStore
{
    Task<EnrolledDevice> EnrollAsync(string name, string platform, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> AuthenticateAsync(string deviceId, string? secret, CancellationToken cancellationToken = default);
    Task<bool> UpdateHeartbeatAsync(string deviceId, string agentVersion, PlatformCapabilityReport capabilities, CancellationToken cancellationToken = default);
    Task<DeviceSnapshot?> FindAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}

public interface ISessionStore
{
    Task<SessionSnapshot> CreateAsync(CreateSessionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingSessionRequest>> GetPendingForDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<ApproveSessionResponse> DecideAsync(string deviceId, string sessionId, ApproveSessionRequest decision, CancellationToken cancellationToken = default);
    Task<SessionStatusResponse> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<bool> SetStateAsync(string sessionId, SessionState state, CancellationToken cancellationToken = default);
}

public interface IAuditStore
{
    Task RecordEventAsync(string eventType, string? deviceId, string? sessionId, string? operatorName, string? detailsJson = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AuditEvent>> GetEventsAsync(string? deviceId = null, string? sessionId = null, int limit = 50, CancellationToken cancellationToken = default);
}
