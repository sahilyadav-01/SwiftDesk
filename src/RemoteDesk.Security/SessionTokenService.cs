using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk.Core;

namespace RemoteDesk.Security;

public sealed record SessionTokenClaims(
    string SessionId,
    string DeviceId,
    SessionRole Role,
    SessionPermission Permissions,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    string TokenId);

public sealed record TokenValidationResult(bool IsValid, SessionTokenClaims? Claims, string? Error)
{
    public static TokenValidationResult Success(SessionTokenClaims claims) => new(true, claims, null);
    public static TokenValidationResult Failure(string error) => new(false, null, error);
}

public sealed class SessionTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public SessionTokenService(string signingKey, TimeProvider? timeProvider = null)
        : this(Encoding.UTF8.GetBytes(signingKey), timeProvider)
    {
    }

    public SessionTokenService(ReadOnlySpan<byte> signingKey, TimeProvider? timeProvider = null)
    {
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("The signing key must be at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Issue(
        string sessionId,
        string deviceId,
        SessionRole role,
        SessionPermission permissions,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!SessionPermissions.IsValid(permissions) || permissions == SessionPermission.None)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Session tickets must last between zero and one hour.");
        }

        var now = _timeProvider.GetUtcNow();
        var claims = new SessionTokenClaims(
            sessionId,
            deviceId,
            role,
            permissions,
            now.ToUnixTimeSeconds(),
            now.Add(lifetime).ToUnixTimeSeconds(),
            Base64Url.Encode(RandomNumberGenerator.GetBytes(16)));

        var payload = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims, JsonOptions));
        var signature = Base64Url.Encode(HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payload)));
        return $"{payload}.{signature}";
    }

    public TokenValidationResult Validate(
        string token,
        SessionRole? expectedRole = null,
        string? expectedSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return TokenValidationResult.Failure("Ticket is missing.");
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return TokenValidationResult.Failure("Ticket format is invalid.");
        }

        try
        {
            var suppliedSignature = Base64Url.Decode(parts[1]);
            var expectedSignature = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(parts[0]));
            if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            {
                return TokenValidationResult.Failure("Ticket signature is invalid.");
            }

            var claims = JsonSerializer.Deserialize<SessionTokenClaims>(Base64Url.Decode(parts[0]), JsonOptions);
            if (claims is null
                || string.IsNullOrWhiteSpace(claims.SessionId)
                || string.IsNullOrWhiteSpace(claims.DeviceId)
                || string.IsNullOrWhiteSpace(claims.TokenId)
                || !SessionPermissions.IsValid(claims.Permissions)
                || claims.Permissions == SessionPermission.None)
            {
                return TokenValidationResult.Failure("Ticket claims are invalid.");
            }

            var now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (claims.ExpiresAtUnixSeconds <= now || claims.IssuedAtUnixSeconds > now + 30)
            {
                return TokenValidationResult.Failure("Ticket is expired or not yet valid.");
            }

            if (expectedRole is not null && claims.Role != expectedRole)
            {
                return TokenValidationResult.Failure("Ticket role is invalid.");
            }

            if (expectedSessionId is not null
                && !string.Equals(claims.SessionId, expectedSessionId, StringComparison.Ordinal))
            {
                return TokenValidationResult.Failure("Ticket is for a different session.");
            }

            return TokenValidationResult.Success(claims);
        }
        catch (FormatException)
        {
            return TokenValidationResult.Failure("Ticket encoding is invalid.");
        }
        catch (JsonException)
        {
            return TokenValidationResult.Failure("Ticket payload is invalid.");
        }
    }
}
