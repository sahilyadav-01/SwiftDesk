using System.Runtime.InteropServices;
using RemoteDesk.Core;
using RemoteDesk.Protocol;

namespace RemoteDesk.Agent;

internal static class UserConsentPrompt
{
    private const uint YesNo = 0x00000004;
    private const uint IconQuestion = 0x00000020;
    private const uint SetForeground = 0x00010000;
    private const uint TopMost = 0x00040000;
    private const int IdYes = 6;

    public static bool Request(PendingSessionRequest request, SessionPermission grantable)
    {
        if (grantable == SessionPermission.None)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var message = $"{request.OperatorName} wants to connect to this computer.\n\n"
                + $"Requested access: {request.RequestedPermissions}\n"
                + $"Allowed access: {grantable}\n\n"
                + "Only allow this connection if you recognize and trust the operator.";

            return MessageBox(
                IntPtr.Zero,
                message,
                "SwiftDesk connection request",
                YesNo | IconQuestion | SetForeground | TopMost) == IdYes;
        }

        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"Connection request {request.SessionId}");
        Console.WriteLine($"Operator: {request.OperatorName}");
        Console.WriteLine($"Requested: {request.RequestedPermissions}");
        Console.WriteLine($"Grantable: {grantable}");
        Console.Write("Approve grantable permissions? [y/N] ");
        return string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);
}
