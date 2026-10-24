using RemoteDesk.Core;
using Xunit;

namespace RemoteDesk.Core.Tests;

public sealed class DeviceIdentityStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsDeviceIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftdesk-identity-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "device-identity.json");
        var expected = new LocalDeviceIdentity("dev_0123456789abcdef", "secret-value");

        try
        {
            DeviceIdentityStore.Save(expected, path);

            var actual = DeviceIdentityStore.Load(path);

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void Load_InvalidIdentity_ReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"swiftdesk-identity-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "device-identity.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"DeviceId\":\"invalid\",\"DeviceSecret\":\"secret\"}");

            Assert.Null(DeviceIdentityStore.Load(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
