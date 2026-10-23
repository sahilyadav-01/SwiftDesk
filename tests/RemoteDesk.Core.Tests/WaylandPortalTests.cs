using RemoteDesk.Core;
using RemoteDesk.Platform.Linux;
using Xunit;

namespace RemoteDesk.Core.Tests;

public sealed class WaylandPortalTests
{
    [Fact]
    public void WaylandPortalCoordinator_ProbesStatusAccurately()
    {
        var coordinator = new WaylandPortalCoordinator();
        var status = coordinator.Probe();

        Assert.NotNull(status);
        Assert.NotNull(status.Details);

        if (!OperatingSystem.IsLinux())
        {
            Assert.False(status.IsWaylandSession);
            Assert.False(status.ScreenCastAvailable);
        }
    }

    [Fact]
    public async Task LinuxPlatformAdapter_WithMockWayland_ReportsWaylandCapabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        var mockWayland = new MockWaylandCoordinator(new WaylandPortalStatus(
            IsWaylandSession: true,
            HasDbusSession: true,
            HasPipeWire: true,
            ScreenCastAvailable: true,
            RemoteDesktopAvailable: true,
            Details: "Mock Wayland active with PipeWire."));

        var adapter = new LinuxPlatformAdapter(waylandCoordinator: mockWayland);
        var report = await adapter.ProbeAsync(ct);

        Assert.Equal("Linux (Wayland)", report.Platform);
        Assert.True(report.ScreenCaptureAvailable);
        Assert.True(report.InputControlAvailable);
        Assert.Contains(report.Requirements, r => r.Contains("Wayland ScreenCast portal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LinuxPlatformAdapter_WaylandWithoutDbus_ReportsDisabledCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        var mockWayland = new MockWaylandCoordinator(new WaylandPortalStatus(
            IsWaylandSession: true,
            HasDbusSession: false,
            HasPipeWire: false,
            ScreenCastAvailable: false,
            RemoteDesktopAvailable: false,
            Details: "Wayland without D-Bus."));

        var adapter = new LinuxPlatformAdapter(waylandCoordinator: mockWayland);
        var report = await adapter.ProbeAsync(ct);

        Assert.Equal("Linux (Wayland)", report.Platform);
        Assert.False(report.ScreenCaptureAvailable);
        Assert.False(report.InputControlAvailable);
    }

    private sealed class MockWaylandCoordinator(WaylandPortalStatus status) : IWaylandPortalCoordinator
    {
        private readonly WaylandPortalStatus _status = status;

        public WaylandPortalStatus Probe() => _status;

        public ValueTask<bool> StartSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_status.ScreenCastAvailable);

        public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void StopSession() { }
    }
}
