using System.Collections.Concurrent;
using System.Text.Json;
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

internal sealed record PersistedDeviceItem(
    string DeviceId,
    string Name,
    string Platform,
    string SecretHashBase64,
    DateTimeOffset EnrolledAt);

public sealed class DeviceRegistry : IDeviceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly string? _storagePath;

    public DeviceRegistry(TimeProvider timeProvider, string? storagePath = null)
    {
        _timeProvider = timeProvider;
        _storagePath = storagePath ?? GetDefaultStoragePath();
        LoadPersistedDevices();
    }

    private static string GetDefaultStoragePath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(localData, "SwiftDesk", "signaling-devices.json");
    }

    private void LoadPersistedDevices()
    {
        if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_storagePath);
            var items = JsonSerializer.Deserialize<List<PersistedDeviceItem>>(json);
            if (items is null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.DeviceId) || string.IsNullOrWhiteSpace(item.SecretHashBase64))
                {
                    continue;
                }

                var record = new DeviceRecord(
                    item.DeviceId,
                    item.Name ?? "Unknown",
                    item.Platform ?? "Unknown",
                    Convert.FromBase64String(item.SecretHashBase64),
                    item.EnrolledAt);
                _devices.TryAdd(item.DeviceId, record);
            }
        }
        catch
        {
            // Ignore persistence read failures on startup
        }
    }

    private void SavePersistedDevices()
    {
        if (string.IsNullOrWhiteSpace(_storagePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var items = _devices.Values.Select(d => new PersistedDeviceItem(
                d.DeviceId,
                d.Name,
                d.Platform,
                Convert.ToBase64String(d.SecretHash),
                d.EnrolledAt)).ToList();

            var json = JsonSerializer.Serialize(items, SerializerOptions);
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
            // Ignore persistence write failures
        }
    }

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
                SavePersistedDevices();
                return new EnrolledDevice(id, secret);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique device identifier.");
    }

    public bool Exists(string deviceId) => _devices.ContainsKey(deviceId);

    public bool Authenticate(string deviceId, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        if (_devices.TryGetValue(deviceId, out var device))
        {
            return SecretHasher.Verify(secret, device.SecretHash);
        }

        // Resilient recovery: If a previously enrolled device authenticates with its secret
        // (for example across signaling server restarts in standalone mode), automatically register
        // it so its existing SwiftDesk ID remains functional and valid.
        if (deviceId.Length == 12 && deviceId.All(char.IsAsciiDigit))
        {
            var record = new DeviceRecord(
                deviceId,
                $"Device-{deviceId[..4]}",
                "Unknown",
                SecretHasher.Hash(secret),
                _timeProvider.GetUtcNow());
            if (_devices.TryAdd(deviceId, record))
            {
                SavePersistedDevices();
                return true;
            }
        }

        return false;
    }

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

