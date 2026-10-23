using System.Runtime.CompilerServices;
using RemoteDesk.Core;

namespace RemoteDesk.Platform.Windows;

public sealed class WindowsPlatformAdapter : IRemotePlatformAdapter
{
    public ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requirements = new List<string>();
        if (!OperatingSystem.IsWindows())
        {
            requirements.Add("This adapter can only run on Windows.");
        }

        requirements.Add("Desktop Duplication or Windows Graphics Capture adapter is not yet linked.");
        requirements.Add("Input injection must respect Windows session and integrity boundaries.");
        return ValueTask.FromResult(new PlatformCapabilityReport(
            "Windows",
            ScreenCaptureAvailable: false,
            InputControlAvailable: false,
            FileTransferAvailable: true,
            RebootAvailable: false,
            requirements));
    }

    public async IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Windows screen capture is an explicit next-phase adapter.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Windows input injection is not enabled in this foundation."));

    public ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Remote reboot requires a separately audited privileged service."));
}

