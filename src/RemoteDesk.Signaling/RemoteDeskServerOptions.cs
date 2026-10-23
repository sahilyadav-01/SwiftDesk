namespace RemoteDesk.Signaling;

public sealed class RemoteDeskServerOptions
{
    public const string SectionName = "RemoteDesk";

    public string BootstrapToken { get; init; } = string.Empty;
    public string OperatorKey { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    public int RequestLifetimeMinutes { get; init; } = 5;
    public int TicketLifetimeMinutes { get; init; } = 10;
    public string? PostgresConnection { get; init; }

    // Enterprise Identity / OIDC Configuration
    public string? OidcIssuer { get; init; }
    public string? OidcAudience { get; init; }
    public bool RequireMfa { get; init; } = true;
    public string? OidcSymmetricKey { get; init; }
    public List<string> OidcRsaPublicKeyPems { get; init; } = [];
    public List<string> AllowedOperatorRoles { get; init; } = ["SupportOperator", "Administrator", "Operator"];
    public bool AllowLegacyOperatorKey { get; init; } = true;
}

