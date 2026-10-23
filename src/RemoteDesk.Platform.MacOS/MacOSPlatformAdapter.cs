using System.Runtime.CompilerServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.MacOS;

public sealed class MacOSPlatformAdapter : IRemotePlatformAdapter
{
    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PlatformCapabilityReport(
            "macOS",
            ScreenCaptureAvailable: false,
            InputControlAvailable: false,
            FileTransferAvailable: true,
            RebootAvailable: false,
            [
                "ScreenCaptureKit requires user-granted Screen Recording permission.",
                "Remote input requires user-granted Accessibility permission.",
                "Distribution requires signing, notarization, and hardened runtime configuration.",
            ]));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("macOS ScreenCaptureKit capture is an explicit next-phase adapter.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("macOS input injection is not enabled in this foundation."));

    public ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Remote reboot requires a separately audited privileged helper."));
}
