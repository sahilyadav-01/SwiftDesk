using RemoteDesk.Core;
using RemoteDesk.Platform.Linux;
using RemoteDesk.Platform.MacOS;
using RemoteDesk.Platform.Windows;

namespace RemoteDesk.Agent;

internal static class PlatformAdapterFactory
{
    public static IRemotePlatformAdapter Create() =>
        OperatingSystem.IsWindows() ? new WindowsPlatformAdapter()
        : OperatingSystem.IsLinux() ? new LinuxPlatformAdapter()
        : OperatingSystem.IsMacOS() ? new MacOSPlatformAdapter()
        : throw new PlatformNotSupportedException("RemoteDesk supports Windows, Linux, and macOS agents.");
}

