using System.Collections.Concurrent;
using RemoteDesk.Core;
using RemoteDesk.Security;

namespace RemoteDesk.Signaling;

public sealed record EnrolledDevice(string DeviceId, string DeviceSecret);

public sealed record DeviceSnapshot(
    string DeviceId,
    string Name,
    string Platform,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastSeenAt,
    string? AgentVersion,
    PlatformCapabilityReport? Capabilities);

internal sealed class DeviceRecord(
    string deviceId,
    string name,
    string platform,
    byte[] secretHash,
    DateTimeOffset enrolledAt)
{
    public object SyncRoot { get; } = new();
    public string DeviceId { get; } = deviceId;
    public string Name { get; } = name;
    public string Platform { get; } = platform;
    public byte[] SecretHash { get; } = secretHash;
    public DateTimeOffset EnrolledAt { get; } = enrolledAt;
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? AgentVersion { get; set; }
    public PlatformCapabilityReport? Capabilities { get; set; }
}

public sealed class DeviceRegistry(TimeProvider timeProvider) : IDeviceStore
{
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider;

    public EnrolledDevice Enroll(string name, string platform)
    {
        var secret = SecretHasher.CreateSecret();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = DeviceIdGenerator.Create();
            var record = new DeviceRecord(
                id,
                name,
                platform,
                SecretHasher.Hash(secret),
                _timeProvider.GetUtcNow());
            if (_devices.TryAdd(id, record))
            {
                return new EnrolledDevice(id, secret);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique device identifier.");
    }

    public bool Exists(string deviceId) => _devices.ContainsKey(deviceId);

    public bool Authenticate(string deviceId, string? secret) =>
        secret is not null
        && _devices.TryGetValue(deviceId, out var device)
        && SecretHasher.Verify(secret, device.SecretHash);

    public bool UpdateHeartbeat(
        string deviceId,
        string agentVersion,
        PlatformCapabilityReport capabilities)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            return false;
        }

        lock (device.SyncRoot)
        {
            device.LastSeenAt = _timeProvider.GetUtcNow();
            device.AgentVersion = agentVersion;
            device.Capabilities = capabilities;
        }

        return true;
    }

    public DeviceSnapshot? Find(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device))
        {
            return null;
        }

        lock (device.SyncRoot)
        {
            return new DeviceSnapshot(
                device.DeviceId,
                device.Name,
                device.Platform,
                device.EnrolledAt,
                device.LastSeenAt,
                device.AgentVersion,
                device.Capabilities);
        }
    }

    public Task<EnrolledDevice> EnrollAsync(string name, string platform, CancellationToken cancellationToken = default) =>
        Task.FromResult(Enroll(name, platform));

    public Task<bool> ExistsAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Exists(deviceId));

    public Task<bool> AuthenticateAsync(string deviceId, string? secret, CancellationToken cancellationToken = default) =>
        Task.FromResult(Authenticate(deviceId, secret));

    public Task<bool> UpdateHeartbeatAsync(
        string deviceId,
        string agentVersion,
        PlatformCapabilityReport capabilities,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(UpdateHeartbeat(deviceId, agentVersion, capabilities));

    public Task<DeviceSnapshot?> FindAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Find(deviceId));

    public Task<IReadOnlyList<DeviceSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeviceSnapshot>>(_devices.Keys.Select(Find).Where(s => s is not null).Select(s => s!).ToArray());
}

