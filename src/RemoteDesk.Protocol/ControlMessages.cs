using RemoteDesk.Core;

namespace RemoteDesk.Protocol;

public enum ControlMessageKind
{
    PeerReady,
    Input,
    FileOffer,
    FileChunk,
    FileComplete,
    FileCancel,
    Reboot,
    Disconnect,
}

public sealed record ControlEnvelope(
    ControlMessageKind Kind,
    string SessionId,
    long Sequence,
    string PayloadJson);

public sealed record PeerReadyMessage(string DeviceId, SessionRole Role);

public sealed record InputCommand(
    RemoteInputKind Kind,
    double NormalizedX,
    double NormalizedY,
    string? Key,
    int WheelDelta = 0);

public sealed record FileTransferOffer(
    string TransferId,
    string FileName,
    long FileSize,
    string Sha256);

public sealed record FileChunkMessage(
    string TransferId,
    long Offset,
    string Base64Data);

public sealed record FileTransferComplete(string TransferId);

public sealed record FileTransferCancel(string TransferId, string Reason);

public sealed record RebootRequest(string Reason);

public sealed record DisconnectMessage(string Reason);

