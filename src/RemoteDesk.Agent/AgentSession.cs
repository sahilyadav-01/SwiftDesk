using System.Collections.Concurrent;
using System.Net.WebSockets;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;

namespace RemoteDesk.Agent;

internal sealed class AgentSession(
    string deviceId,
    PendingSessionRequest request,
    SessionPermission grantedPermissions,
    IRemotePlatformAdapter platform,
    string? downloadDirectory)
{
    private readonly ConcurrentDictionary<string, SafeFileReceiver> _transfers = new(StringComparer.Ordinal);

    public async Task RunAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var ready = new PeerReadyMessage(deviceId, SessionRole.Agent);
        await WebSocketProtocol.SendAsync(
            socket,
            new ControlEnvelope(
                ControlMessageKind.PeerReady,
                request.SessionId,
                0,
                ProtocolJson.Serialize(ready)),
            cancellationToken);

        long lastSequence = -1;
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var envelope = await WebSocketProtocol.ReceiveAsync(socket, cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                if (envelope.SessionId != request.SessionId || envelope.Sequence <= lastSequence)
                {
                    throw new InvalidDataException("Received an invalid or replayed control message.");
                }

                lastSequence = envelope.Sequence;
                await HandleAsync(envelope, cancellationToken);
            }
        }
        finally
        {
            foreach (var receiver in _transfers.Values)
            {
                await receiver.DisposeAsync();
            }

            _transfers.Clear();
        }
    }

    private async Task HandleAsync(ControlEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Kind)
        {
            case ControlMessageKind.PeerReady:
                return;

            case ControlMessageKind.Input:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.ControlInput);
                var command = ProtocolJson.Deserialize<InputCommand>(envelope.PayloadJson);
                ProtocolValidation.Validate(command);
                await platform.InjectInputAsync(
                    new RemoteInput(
                        command.Kind,
                        command.NormalizedX,
                        command.NormalizedY,
                        command.Key,
                        command.WheelDelta),
                    cancellationToken);
                return;
            }

            case ControlMessageKind.FileOffer:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.TransferFiles);
                if (downloadDirectory is null)
                {
                    throw new UnauthorizedAccessException("No file destination was approved by the device owner.");
                }

                var offer = ProtocolJson.Deserialize<FileTransferOffer>(envelope.PayloadJson);
                var receiver = SafeFileReceiver.Create(downloadDirectory, offer);
                if (!_transfers.TryAdd(offer.TransferId, receiver))
                {
                    await receiver.DisposeAsync();
                    throw new InvalidDataException("Transfer identifier is already active.");
                }

                return;
            }

            case ControlMessageKind.FileChunk:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.TransferFiles);
                var chunk = ProtocolJson.Deserialize<FileChunkMessage>(envelope.PayloadJson);
                if (!_transfers.TryGetValue(chunk.TransferId, out var receiver))
                {
                    throw new InvalidDataException("Transfer is not active.");
                }

                var bytes = Convert.FromBase64String(chunk.Base64Data);
                if (bytes.Length > 48 * 1024)
                {
                    throw new InvalidDataException("File chunk exceeds the 48 KiB limit.");
                }

                await receiver.WriteChunkAsync(chunk.Offset, bytes, cancellationToken);
                return;
            }

            case ControlMessageKind.FileComplete:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.TransferFiles);
                var complete = ProtocolJson.Deserialize<FileTransferComplete>(envelope.PayloadJson);
                if (!_transfers.TryRemove(complete.TransferId, out var receiver))
                {
                    throw new InvalidDataException("Transfer is not active.");
                }

                await using (receiver)
                {
                    var path = await receiver.CompleteAsync(cancellationToken);
                    Console.WriteLine($"Received file: {path}");
                }

                return;
            }

            case ControlMessageKind.FileCancel:
            {
                var cancel = ProtocolJson.Deserialize<FileTransferCancel>(envelope.PayloadJson);
                if (_transfers.TryRemove(cancel.TransferId, out var receiver))
                {
                    await receiver.DisposeAsync();
                }

                return;
            }

            case ControlMessageKind.Reboot:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.RebootDevice);
                var reboot = ProtocolJson.Deserialize<RebootRequest>(envelope.PayloadJson);
                ProtocolValidation.RequireText(reboot.Reason, ProtocolValidation.MaxReasonLength, nameof(reboot.Reason));
                await platform.RebootAsync(reboot.Reason, cancellationToken);
                return;
            }

            case ControlMessageKind.Disconnect:
                throw new OperationCanceledException("Controller requested disconnect.", cancellationToken);

            default:
                throw new InvalidDataException("Unknown control message kind.");
        }
    }
}

