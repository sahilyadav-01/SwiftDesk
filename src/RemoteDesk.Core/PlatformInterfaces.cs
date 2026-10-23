namespace RemoteDesk.Core;

public interface IRemotePlatformAdapter
{
    ValueTask<PlatformCapabilityReport> ProbeAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ScreenFrame> CaptureAsync(
        int framesPerSecond,
        CancellationToken cancellationToken = default);

    ValueTask InjectInputAsync(RemoteInput input, CancellationToken cancellationToken = default);

    ValueTask RebootAsync(string reason, CancellationToken cancellationToken = default);
}

public enum RemoteInputKind
{
    PointerMove,
    PointerButtonDown,
    PointerButtonUp,
    MouseWheel,
    KeyDown,
    KeyUp,
}

public sealed record RemoteInput(
    RemoteInputKind Kind,
    double NormalizedX,
    double NormalizedY,
    string? Key,
    int WheelDelta = 0);

