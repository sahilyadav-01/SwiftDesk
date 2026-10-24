using System.Text;
using System.Text.Json;

namespace RemoteDesk.Core;

public sealed record LocalDeviceIdentity(string DeviceId, string DeviceSecret);

public static class DeviceIdentityStore
{
    private const string IdentityFileEnvironmentVariable = "REMOTEDESK_IDENTITY_FILE";

    public static string GetIdentityPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(IdentityFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(localData, "SwiftDesk", "device-identity.json");
    }

    public static LocalDeviceIdentity? Load(string? path = null)
    {
        var identityPath = path ?? GetIdentityPath();
        try
        {
            if (!File.Exists(identityPath))
            {
                return null;
            }

            var json = File.ReadAllText(identityPath, Encoding.UTF8);
            var identity = JsonSerializer.Deserialize<LocalDeviceIdentity>(json);
            return IsValid(identity) ? identity : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    public static void Save(LocalDeviceIdentity identity, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsValid(identity))
        {
            throw new ArgumentException("Device identity is incomplete or invalid.", nameof(identity));
        }

        var identityPath = path ?? GetIdentityPath();
        var directory = Path.GetDirectoryName(identityPath)
            ?? throw new ArgumentException("Identity path must include a directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{identityPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(identity);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            RestrictToCurrentUser(temporaryPath);
            File.Move(temporaryPath, identityPath, true);
            RestrictToCurrentUser(identityPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValid(LocalDeviceIdentity? identity) =>
        identity is not null
        && identity.DeviceId.StartsWith("dev_", StringComparison.Ordinal)
        && identity.DeviceId.Length <= 64
        && !string.IsNullOrWhiteSpace(identity.DeviceSecret);

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
