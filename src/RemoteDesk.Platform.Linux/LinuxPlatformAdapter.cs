using System.Runtime.CompilerServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.Linux;

public sealed class LinuxPlatformAdapter : IRemotePlatformAdapter
{
    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionType = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? "X11 or headless"
            : "Wayland";
        return ValueTask.FromResult(new PlatformCapabilityReport(
            $"Linux ({sessionType})",
            ScreenCaptureAvailable: false,
            InputControlAvailable: false,
            FileTransferAvailable: true,
            RebootAvailable: false,
            [
                "Wayland capture requires PipeWire plus an xdg-desktop-portal user grant.",
                "Synthetic input varies by compositor and must never bypass desktop policy.",
            ]));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Linux PipeWire/X11 capture is an explicit next-phase adapter.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Linux input injection is not enabled in this foundation."));

    public ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Remote reboot requires a separately audited privileged service."));
}

