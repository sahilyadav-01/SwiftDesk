using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Security;

public static class SecretHasher
{
    public static string CreateSecret(int bytes = 32)
    {
        if (bytes < 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Secrets must contain at least 256 bits of entropy.");
        }

        return Base64Url.Encode(RandomNumberGenerator.GetBytes(bytes));
    }

    public static byte[] Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public static bool Verify(string candidate, ReadOnlySpan<byte> expectedHash)
    {
        if (string.IsNullOrEmpty(candidate) || expectedHash.Length != 32)
        {
            return false;
        }

        var actual = Hash(candidate);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}

