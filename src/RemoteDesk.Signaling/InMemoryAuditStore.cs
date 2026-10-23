using System.Collections.Concurrent;

namespace RemoteDesk.Signaling;

public sealed class InMemoryAuditStore(TimeProvider timeProvider) : IAuditStore
{
    private readonly ConcurrentBag<AuditEvent> _events = [];
    private readonly TimeProvider _timeProvider = timeProvider;
    private long _idSequence;

    public Task RecordEventAsync(
        string eventType,
        string? deviceId,
        string? sessionId,
        string? operatorName,
        string? detailsJson = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _idSequence);
        var auditEvent = new AuditEvent(
            id,
            eventType,
            deviceId,
            sessionId,
            operatorName,
            detailsJson,
            _timeProvider.GetUtcNow());

        _events.Add(auditEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> GetEventsAsync(
        string? deviceId = null,
        string? sessionId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var query = _events.AsEnumerable();

        if (!string.IsNullOrEmpty(deviceId))
        {
            query = query.Where(e => e.DeviceId == deviceId);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            query = query.Where(e => e.SessionId == sessionId);
        }

        var results = query
            .OrderByDescending(e => e.CreatedAt)
            .Take(boundedLimit)
            .ToArray();

        return Task.FromResult<IReadOnlyList<AuditEvent>>(results);
    }
}
