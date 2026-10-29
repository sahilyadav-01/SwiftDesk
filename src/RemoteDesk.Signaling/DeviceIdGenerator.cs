using System.Security.Cryptography;
using System.Globalization;

namespace RemoteDesk.Signaling;

internal static class DeviceIdGenerator
{
    public const int Length = 12;

    public static string Create() => string.Concat(
        RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture),
        RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture));
}
