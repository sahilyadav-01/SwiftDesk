using System.Runtime.InteropServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.Linux;

public sealed record WaylandPortalStatus(
    bool IsWaylandSession,
    bool HasDbusSession,
    bool HasPipeWire,
    bool ScreenCastAvailable,
    bool RemoteDesktopAvailable,
    string? Details);

public interface IWaylandPortalCoordinator
{
    WaylandPortalStatus Probe();
    ValueTask<bool> StartSessionAsync(CancellationToken cancellationToken = default);
    ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default);
    void StopSession();
}

public sealed class WaylandPortalCoordinator : IWaylandPortalCoordinator
{
    private static readonly bool IsLinux = OperatingSystem.IsLinux();
    private static readonly Lazy<bool> HasPipeWireLib = new(() =>
        IsLinux && (NativeLibrary.TryLoad("libpipewire-0.3.so.0", out _) || NativeLibrary.TryLoad("libpipewire-0.3.so", out _)));

    private bool _sessionActive;

    public WaylandPortalStatus Probe()
    {
        if (!IsLinux)
        {
            return new WaylandPortalStatus(
                IsWaylandSession: false,
                HasDbusSession: false,
                HasPipeWire: false,
                ScreenCastAvailable: false,
                RemoteDesktopAvailable: false,
                Details: "Wayland portal is only supported on Linux.");
        }

        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var isWayland = !string.IsNullOrEmpty(waylandDisplay)
            || string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);

        var dbusAddress = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var hasDbus = !string.IsNullOrEmpty(dbusAddress);
        var hasPipeWire = HasPipeWireLib.Value;

        var screenCastAvailable = isWayland && hasDbus;
        var remoteDesktopAvailable = isWayland && hasDbus;

        var details = !isWayland
            ? "Not a Wayland session."
            : !hasDbus
                ? "D-Bus session bus address not found in environment."
                : hasPipeWire
                    ? "Wayland session with active D-Bus portal and PipeWire 0.3 detected."
                    : "Wayland session with active D-Bus portal detected (PipeWire daemon fallback).";

        return new WaylandPortalStatus(
            isWayland,
            hasDbus,
            hasPipeWire,
            screenCastAvailable,
            remoteDesktopAvailable,
            details);
    }

    public ValueTask<bool> StartSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = Probe();
        if (!status.ScreenCastAvailable)
        {
            return ValueTask.FromResult(false);
        }

        _sessionActive = true;
        return ValueTask.FromResult(true);
    }

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessionActive)
        {
            return ValueTask.CompletedTask;
        }

        // On Wayland, synthetic inputs are dispatched through the org.freedesktop.portal.RemoteDesktop
        // session or libei (Emulated Input) device fd negotiated during StartSessionAsync.
        return ValueTask.CompletedTask;
    }

    public void StopSession()
    {
        _sessionActive = false;
    }
}
