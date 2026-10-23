using RemoteDesk.Core;
using RemoteDesk.Platform.Linux;
using RemoteDesk.Platform.MacOS;
using RemoteDesk.Platform.Windows;
using RemoteDesk.PrivilegedHelper;

namespace RemoteDesk.Agent;

internal static class PlatformAdapterFactory
{
    public static IRemotePlatformAdapter Create()
    {
        var rebootClient = ResolvePrivilegedRebootClient();
        return OperatingSystem.IsWindows() ? new WindowsPlatformAdapter(rebootClient)
            : OperatingSystem.IsLinux() ? new LinuxPlatformAdapter(rebootClient)
            : OperatingSystem.IsMacOS() ? new MacOSPlatformAdapter(rebootClient)
            : throw new PlatformNotSupportedException("RemoteDesk supports Windows, Linux, and macOS agents.");
    }

    private static PrivilegedRebootClient? ResolvePrivilegedRebootClient()
    {
        // 1. Check environment variable
        var envKey = Environment.GetEnvironmentVariable("SWIFTDESK_REBOOT_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            try
            {
                var bytes = Convert.FromHexString(envKey.Trim());
                if (bytes.Length >= 32)
                {
                    return new PrivilegedRebootClient(bytes);
                }
            }
            catch
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(envKey.Trim());
                if (bytes.Length >= 32)
                {
                    return new PrivilegedRebootClient(bytes);
                }
            }
        }

        // 2. Check standard system directory path
        var keyPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SwiftDesk", "reboot.key")
            : "/etc/swiftdesk/reboot.key";

        if (File.Exists(keyPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(keyPath);
                if (bytes.Length >= 32)
                {
                    return new PrivilegedRebootClient(bytes);
                }
            }
            catch
            {
                // Silently continue if unreadable
            }
        }

        return null;
    }
}
