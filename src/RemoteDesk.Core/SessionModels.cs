namespace RemoteDesk.Core;

public enum SessionState
{
    Pending,
    Approved,
    Active,
    Declined,
    Disconnected,
    Expired,
}

public enum SessionRole
{
    Controller,
    Agent,
}

public sealed record SessionSnapshot(
    string SessionId,
    string DeviceId,
    string OperatorName,
    SessionPermission RequestedPermissions,
    SessionPermission GrantedPermissions,
    SessionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record PlatformCapabilityReport(
    string Platform,
    bool ScreenCaptureAvailable,
    bool InputControlAvailable,
    bool FileTransferAvailable,
    bool RebootAvailable,
    IReadOnlyList<string> Requirements);

public sealed record ScreenFrame(
    long Sequence,
    int Width,
    int Height,
    string PixelFormat,
    ReadOnlyMemory<byte> Data,
    DateTimeOffset CapturedAt);

