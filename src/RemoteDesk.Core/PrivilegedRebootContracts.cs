namespace RemoteDesk.Core;

public sealed record PrivilegedRebootRequest(
    string SessionId,
    string Reason,
    long TimestampUnixMs,
    string Nonce,
    string Signature);

public sealed record PrivilegedRebootResponse(
    bool Success,
    string Message,
    string? ErrorCode = null)
{
    public static PrivilegedRebootResponse Succeeded(string message = "Reboot initiated.") =>
        new(true, message, null);

    public static PrivilegedRebootResponse Failed(string errorCode, string message) =>
        new(false, message, errorCode);
}

public interface IPrivilegedRebootClient
{
    bool IsAvailable { get; }

    ValueTask<PrivilegedRebootResponse> RequestRebootAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default);
}
