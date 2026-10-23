namespace RemoteDesk.Signaling;

public sealed class RemoteDeskServerOptions
{
    public const string SectionName = "RemoteDesk";

    public string BootstrapToken { get; init; } = string.Empty;
    public string OperatorKey { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    public int RequestLifetimeMinutes { get; init; } = 5;
    public int TicketLifetimeMinutes { get; init; } = 10;
}

