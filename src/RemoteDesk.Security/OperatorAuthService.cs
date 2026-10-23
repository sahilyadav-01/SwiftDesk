using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk.Security;

public sealed record OperatorIdentity(
    string Subject,
    string DisplayName,
    string? Email,
    IReadOnlyList<string> Roles,
    bool IsMfaAuthenticated,
    IReadOnlyList<string> AuthenticationMethods,
    string? TokenId,
    DateTimeOffset ExpiresAt);

public sealed record OperatorAuthResult(
    bool IsSuccess,
    OperatorIdentity? Identity,
    string? FailureReason,
    string? Description)
{
    public static OperatorAuthResult Success(OperatorIdentity identity) =>
        new(true, identity, null, null);

    public static OperatorAuthResult Failure(string reason, string? description = null) =>
        new(false, null, reason, description);
}

public sealed class OidcOperatorOptions
{
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public bool RequireMfa { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = ["SupportOperator", "Administrator", "Operator"];
    public List<string> RecognizedMfaMethods { get; set; } = ["mfa", "otp", "hwk", "fido", "fido2", "face", "sms", "pin"];
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);
    public string? SymmetricKey { get; set; }
    public List<string> RsaPublicKeyPems { get; set; } = [];
    public bool AllowLegacyKeyInDevelopment { get; set; }
    public string? LegacyOperatorKey { get; set; }
}

public interface IOperatorAuthService
{
    OperatorAuthResult Authenticate(string? authorizationHeader, string? legacyOperatorKeyHeader = null);
}

public sealed class OperatorAuthService(OidcOperatorOptions options, TimeProvider? timeProvider = null) : IOperatorAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OidcOperatorOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public OperatorAuthResult Authenticate(string? authorizationHeader, string? legacyOperatorKeyHeader = null)
    {
        // 1. Check Bearer JWT token if present
        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            var header = authorizationHeader.Trim();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = header["Bearer ".Length..].Trim();
                return ValidateJwt(token);
            }
        }

        // 2. Fallback to legacy key only if explicitly configured for development
        if (_options.AllowLegacyKeyInDevelopment && !string.IsNullOrWhiteSpace(_options.LegacyOperatorKey))
        {
            if (!string.IsNullOrWhiteSpace(legacyOperatorKeyHeader))
            {
                var supplied = Encoding.UTF8.GetBytes(legacyOperatorKeyHeader.Trim());
                var expected = Encoding.UTF8.GetBytes(_options.LegacyOperatorKey.Trim());
                if (supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected))
                {
                    var devIdentity = new OperatorIdentity(
                        Subject: "local-dev-operator",
                        DisplayName: "Local Development Operator",
                        Email: "dev@localhost",
                        Roles: ["Administrator", "SupportOperator"],
                        IsMfaAuthenticated: true,
                        AuthenticationMethods: ["local_key"],
                        TokenId: "legacy-dev-token",
                        ExpiresAt: _timeProvider.GetUtcNow().AddHours(1));

                    return OperatorAuthResult.Success(devIdentity);
                }
            }
        }

        return OperatorAuthResult.Failure("unauthorized", "Missing or invalid operator authorization credentials.");
    }

    public OperatorAuthResult ValidateJwt(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return OperatorAuthResult.Failure("invalid_token", "Bearer token is empty.");
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return OperatorAuthResult.Failure("invalid_token", "JWT must consist of header, payload, and signature.");
        }

        JsonDocument headerDoc;
        JsonDocument payloadDoc;
        try
        {
            var headerBytes = Base64Url.Decode(parts[0]);
            var payloadBytes = Base64Url.Decode(parts[1]);
            headerDoc = JsonDocument.Parse(headerBytes);
            payloadDoc = JsonDocument.Parse(payloadBytes);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return OperatorAuthResult.Failure("invalid_token", "JWT contains invalid base64url or JSON content.");
        }

        using (headerDoc)
        using (payloadDoc)
        {
            var headerRoot = headerDoc.RootElement;
            var payloadRoot = payloadDoc.RootElement;

            // Algorithm check
            if (!headerRoot.TryGetProperty("alg", out var algElement))
            {
                return OperatorAuthResult.Failure("invalid_header", "Token header missing alg claim.");
            }

            var alg = algElement.GetString();
            if (string.IsNullOrWhiteSpace(alg))
            {
                return OperatorAuthResult.Failure("invalid_header", "Token header alg is empty.");
            }

            // Verify signature
            var signatureValid = VerifySignature(alg, parts[0], parts[1], parts[2]);
            if (!signatureValid)
            {
                return OperatorAuthResult.Failure("invalid_signature", "Token cryptographic signature verification failed.");
            }

            var now = _timeProvider.GetUtcNow();

            // Validate Expiry (exp)
            if (payloadRoot.TryGetProperty("exp", out var expElement) && expElement.TryGetInt64(out var expSeconds))
            {
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
                if (expiresAt + _options.ClockSkew <= now)
                {
                    return OperatorAuthResult.Failure("token_expired", "Token has expired.");
                }
            }
            else
            {
                return OperatorAuthResult.Failure("missing_exp", "Token is missing an expiration (exp) claim.");
            }

            // Validate Not Before (nbf)
            if (payloadRoot.TryGetProperty("nbf", out var nbfElement) && nbfElement.TryGetInt64(out var nbfSeconds))
            {
                var notBefore = DateTimeOffset.FromUnixTimeSeconds(nbfSeconds);
                if (notBefore - _options.ClockSkew > now)
                {
                    return OperatorAuthResult.Failure("token_not_yet_valid", "Token is not yet active.");
                }
            }

            // Validate Issuer (iss)
            if (!string.IsNullOrWhiteSpace(_options.Issuer))
            {
                if (!payloadRoot.TryGetProperty("iss", out var issElement)
                    || !string.Equals(issElement.GetString(), _options.Issuer, StringComparison.Ordinal))
                {
                    return OperatorAuthResult.Failure("untrusted_issuer", $"Token issuer does not match '{_options.Issuer}'.");
                }
            }

            // Validate Audience (aud)
            if (!string.IsNullOrWhiteSpace(_options.Audience))
            {
                var audienceMatches = false;
                if (payloadRoot.TryGetProperty("aud", out var audElement))
                {
                    if (audElement.ValueKind == JsonValueKind.String)
                    {
                        audienceMatches = string.Equals(audElement.GetString(), _options.Audience, StringComparison.Ordinal);
                    }
                    else if (audElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in audElement.EnumerateArray())
                        {
                            if (string.Equals(item.GetString(), _options.Audience, StringComparison.Ordinal))
                            {
                                audienceMatches = true;
                                break;
                            }
                        }
                    }
                }

                if (!audienceMatches)
                {
                    return OperatorAuthResult.Failure("invalid_audience", $"Token audience does not match '{_options.Audience}'.");
                }
            }

            // Extract Subject
            var subject = payloadRoot.TryGetProperty("sub", out var subProp) ? subProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(subject))
            {
                return OperatorAuthResult.Failure("missing_sub", "Token is missing a subject (sub) claim.");
            }

            // Extract Display Name & Email
            var displayName = payloadRoot.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = payloadRoot.TryGetProperty("preferred_username", out var userProp) ? userProp.GetString() : subject;
            }

            var email = payloadRoot.TryGetProperty("email", out var emailProp) ? emailProp.GetString() : null;
            var tokenId = payloadRoot.TryGetProperty("jti", out var jtiProp) ? jtiProp.GetString() : null;

            // Extract Roles
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExtractStrings(payloadRoot, "roles", roles);
            ExtractStrings(payloadRoot, "role", roles);
            ExtractStrings(payloadRoot, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", roles);
            ExtractStrings(payloadRoot, "groups", roles);

            // Role enforcement
            if (_options.AllowedRoles.Count > 0)
            {
                var hasAllowedRole = _options.AllowedRoles.Any(roles.Contains);
                if (!hasAllowedRole)
                {
                    return OperatorAuthResult.Failure("insufficient_role", "Operator lacks required role authorization.");
                }
            }

            // Extract Authentication Methods References (amr) and Authentication Context Class Reference (acr)
            var amrList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExtractStrings(payloadRoot, "amr", amrList);
            if (payloadRoot.TryGetProperty("acr", out var acrProp) && acrProp.GetString() is { } acr)
            {
                amrList.Add(acr);
            }

            // Check MFA
            var isMfa = amrList.Any(method => _options.RecognizedMfaMethods.Contains(method, StringComparer.OrdinalIgnoreCase));
            if (_options.RequireMfa && !isMfa)
            {
                return OperatorAuthResult.Failure("mfa_required", "Enterprise operator authentication requires Multi-Factor Authentication (MFA).");
            }

            var tokenExpiry = expElement.TryGetInt64(out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : now.AddHours(1);

            var identity = new OperatorIdentity(
                Subject: subject,
                DisplayName: displayName ?? subject,
                Email: email,
                Roles: roles.ToList(),
                IsMfaAuthenticated: isMfa,
                AuthenticationMethods: amrList.ToList(),
                TokenId: tokenId,
                ExpiresAt: tokenExpiry);

            return OperatorAuthResult.Success(identity);
        }
    }

    private bool VerifySignature(string alg, string headerBase64, string payloadBase64, string signatureBase64)
    {
        var dataBytes = Encoding.ASCII.GetBytes($"{headerBase64}.{payloadBase64}");
        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64Url.Decode(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (string.Equals(alg, "HS256", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_options.SymmetricKey))
            {
                return false;
            }

            var keyBytes = Encoding.UTF8.GetBytes(_options.SymmetricKey);
            var expectedHash = HMACSHA256.HashData(keyBytes, dataBytes);
            return CryptographicOperations.FixedTimeEquals(signatureBytes, expectedHash);
        }

        if (string.Equals(alg, "RS256", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.RsaPublicKeyPems.Count == 0)
            {
                return false;
            }

            foreach (var pem in _options.RsaPublicKeyPems)
            {
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    if (rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Continue to next key
                }
            }

            return false;
        }

        return false;
    }

    private static void ExtractStrings(JsonElement element, string propertyName, HashSet<string> target)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value.Trim());
            }
        }
        else if (prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    target.Add(value.Trim());
                }
            }
        }
    }

    public static string CreateToken(
        string subject,
        string displayName,
        string[] roles,
        string[]? amr = null,
        string? issuer = null,
        string? audience = null,
        string? symmetricKey = null,
        RSA? rsaPrivateKey = null,
        TimeSpan? lifetime = null,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var now = time.GetUtcNow();
        var expiry = now.Add(lifetime ?? TimeSpan.FromHours(1));

        var headerObj = new Dictionary<string, object>
        {
            ["typ"] = "JWT",
            ["alg"] = rsaPrivateKey != null ? "RS256" : "HS256",
        };

        var payloadObj = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["name"] = displayName,
            ["roles"] = roles,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiry.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (!string.IsNullOrWhiteSpace(issuer)) payloadObj["iss"] = issuer;
        if (!string.IsNullOrWhiteSpace(audience)) payloadObj["aud"] = audience;
        if (amr != null && amr.Length > 0) payloadObj["amr"] = amr;

        var headerJson = JsonSerializer.Serialize(headerObj, JsonOptions);
        var payloadJson = JsonSerializer.Serialize(payloadObj, JsonOptions);

        var headerB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payloadB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{headerB64}.{payloadB64}";
        var signingBytes = Encoding.ASCII.GetBytes(signingInput);

        byte[] signature;
        if (rsaPrivateKey != null)
        {
            signature = rsaPrivateKey.SignData(signingBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else if (!string.IsNullOrWhiteSpace(symmetricKey))
        {
            signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(symmetricKey), signingBytes);
        }
        else
        {
            throw new ArgumentException("Either symmetricKey or rsaPrivateKey must be supplied to create a token.");
        }

        var sigB64 = Base64Url.Encode(signature);
        return $"{signingInput}.{sigB64}";
    }
}
