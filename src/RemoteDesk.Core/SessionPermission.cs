namespace RemoteDesk.Core;

[Flags]
public enum SessionPermission
{
    None = 0,
    ViewScreen = 1 << 0,
    ControlInput = 1 << 1,
    TransferFiles = 1 << 2,
    RebootDevice = 1 << 3,
}

public static class SessionPermissions
{
    public const SessionPermission All = SessionPermission.ViewScreen
        | SessionPermission.ControlInput
        | SessionPermission.TransferFiles
        | SessionPermission.RebootDevice;

    public static bool IsValid(SessionPermission value) => (value & ~All) == 0;

    public static bool IsSubsetOf(SessionPermission requested, SessionPermission allowed) =>
        IsValid(requested) && requested != SessionPermission.None && (requested & ~allowed) == 0;
}

