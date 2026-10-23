using System.Security.Cryptography;
using RemoteDesk.Security;
using Xunit;

namespace RemoteDesk.Security.Tests;

public sealed class OperatorAuthServiceTests
{
    private const string TestSymmetricKey = "enterprise-operator-secret-key-at-least-32-chars!";
    private const string TestIssuer = "https://identity.enterprise.example.com";
    private const string TestAudience = "swiftdesk-signaling-api";

    [Fact]
    public void ValidToken_WithRoleAndMfa_AuthenticatesSuccessfully()
    {
        var options = new OidcOperatorOptions
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            RequireMfa = true,
            SymmetricKey = TestSymmetricKey,
            AllowedRoles = ["SupportOperator", "Administrator"],
        };

        var service = new OperatorAuthService(options);
        var token = OperatorAuthService.CreateToken(
            subject: "operator-007",
            displayName: "James Operator",
            roles: ["SupportOperator"],
            amr: ["pwd", "mfa"],
            issuer: TestIssuer,
            audience: TestAudience,
            symmetricKey: TestSymmetricKey);

        var result = service.Authenticate($"Bearer {token}");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Identity);
        Assert.Equal("operator-007", result.Identity.Subject);
        Assert.Equal("James Operator", result.Identity.DisplayName);
        Assert.True(result.Identity.IsMfaAuthenticated);
        Assert.Contains("SupportOperator", result.Identity.Roles);
    }

    [Fact]
    public void TokenMissingMfa_WhenMfaRequired_IsRejected()
    {
        var options = new OidcOperatorOptions
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            RequireMfa = true,
            SymmetricKey = TestSymmetricKey,
        };

        var service = new OperatorAuthService(options);
        var token = OperatorAuthService.CreateToken(
            subject: "operator-001",
            displayName: "Single Factor Operator",
            roles: ["SupportOperator"],
            amr: ["pwd"], // Only password, no MFA
            issuer: TestIssuer,
            audience: TestAudience,
            symmetricKey: TestSymmetricKey);

        var result = service.Authenticate($"Bearer {token}");

        Assert.False(result.IsSuccess);
        Assert.Equal("mfa_required", result.FailureReason);
    }

    [Fact]
    public void TokenWithInsufficientRole_IsRejected()
    {
        var options = new OidcOperatorOptions
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            RequireMfa = false,
            SymmetricKey = TestSymmetricKey,
            AllowedRoles = ["Administrator"],
        };

        var service = new OperatorAuthService(options);
        var token = OperatorAuthService.CreateToken(
            subject: "guest-user",
            displayName: "Guest User",
            roles: ["GuestViewer"], // Not in AllowedRoles
            issuer: TestIssuer,
            audience: TestAudience,
            symmetricKey: TestSymmetricKey);

        var result = service.Authenticate($"Bearer {token}");

        Assert.False(result.IsSuccess);
        Assert.Equal("insufficient_role", result.FailureReason);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var options = new OidcOperatorOptions
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            RequireMfa = false,
            SymmetricKey = TestSymmetricKey,
            ClockSkew = TimeSpan.FromSeconds(5),
        };

        var service = new OperatorAuthService(options, clock);
        var token = OperatorAuthService.CreateToken(
            subject: "operator-123",
            displayName: "Op 123",
            roles: ["SupportOperator"],
            issuer: TestIssuer,
            audience: TestAudience,
            symmetricKey: TestSymmetricKey,
            lifetime: TimeSpan.FromMinutes(10),
            timeProvider: clock);

        clock.Advance(TimeSpan.FromMinutes(15)); // Expired

        var result = service.Authenticate($"Bearer {token}");

        Assert.False(result.IsSuccess);
        Assert.Equal("token_expired", result.FailureReason);
    }

    [Fact]
    public void RsaSignedToken_ValidatesWithPublicKeyPem()
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var options = new OidcOperatorOptions
        {
            Issuer = TestIssuer,
            Audience = TestAudience,
            RequireMfa = true,
            RsaPublicKeyPems = [publicKeyPem],
            AllowedRoles = ["SupportOperator"],
        };

        var service = new OperatorAuthService(options);
        var token = OperatorAuthService.CreateToken(
            subject: "rsa-operator",
            displayName: "RSA Operator",
            roles: ["SupportOperator"],
            amr: ["fido2"],
            issuer: TestIssuer,
            audience: TestAudience,
            rsaPrivateKey: rsa);

        var result = service.Authenticate($"Bearer {token}");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Identity);
        Assert.Equal("rsa-operator", result.Identity.Subject);
        Assert.True(result.Identity.IsMfaAuthenticated);
    }

    [Fact]
    public void LegacyKey_WhenAllowedInDevelopment_Succeeds()
    {
        var options = new OidcOperatorOptions
        {
            AllowLegacyKeyInDevelopment = true,
            LegacyOperatorKey = "local-dev-operator-key",
        };

        var service = new OperatorAuthService(options);
        var result = service.Authenticate(null, "local-dev-operator-key");

        Assert.True(result.IsSuccess);
        Assert.Equal("local-dev-operator", result.Identity?.Subject);
    }

    [Fact]
    public void LegacyKey_WhenDisabled_IsRejected()
    {
        var options = new OidcOperatorOptions
        {
            AllowLegacyKeyInDevelopment = false,
            LegacyOperatorKey = "local-dev-operator-key",
        };

        var service = new OperatorAuthService(options);
        var result = service.Authenticate(null, "local-dev-operator-key");

        Assert.False(result.IsSuccess);
        Assert.Equal("unauthorized", result.FailureReason);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
