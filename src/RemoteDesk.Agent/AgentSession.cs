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
    string? downloadDirectory) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, SafeFileReceiver> _transfers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly DataChannelMessageFramer _relayFrameFramer = new();
    private long _sequence;
    private RtcPeerCoordinator? _rtc;
    private CancellationTokenSource? _sessionCts;
    private Task? _screenStreamTask;

    public async Task RunAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionCts = linkedCts;

        var ready = new PeerReadyMessage(deviceId, SessionRole.Agent);
        await SendEnvelopeAsync(socket, ControlMessageKind.PeerReady, ready, cancellationToken);

        if (grantedPermissions.HasFlag(SessionPermission.ViewScreen))
        {
            _rtc = new RtcPeerCoordinator();
            _rtc.OnLocalIceCandidate += async candidate =>
            {
                try
                {
                    await SendEnvelopeAsync(socket, ControlMessageKind.RtcIceCandidate, candidate, _sessionCts.Token);
                }
                catch
                {
                    // Ignore transient network errors during ICE exchange
                }
            };

            _screenStreamTask = Task.Run(() => StreamScreenLoopAsync(socket, _sessionCts.Token), _sessionCts.Token);
        }

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
                await HandleAsync(socket, envelope, cancellationToken);
            }
        }
        finally
        {
            linkedCts.Cancel();
            if (_screenStreamTask is not null)
            {
                try
                {
                    await _screenStreamTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during teardown
                }
            }

            foreach (var receiver in _transfers.Values)
            {
                await receiver.DisposeAsync();
            }

            _transfers.Clear();
            if (_rtc is not null)
            {
                await _rtc.DisposeAsync();
                _rtc = null;
            }
        }
    }

    private async Task StreamScreenLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in platform.CaptureAsync(15, cancellationToken))
            {
                try
                {
                    var frameBytes = frame.Data.ToArray();

                    if (_rtc is not null && _rtc.TrySendData(frameBytes))
                    {
                        continue;
                    }

                    // Reliable WebSocket relay fallback for networks where direct WebRTC cannot connect.
                    foreach (var packet in _relayFrameFramer.Encode(frameBytes, 36 * 1024))
                    {
                        var message = new ScreenFrameChunkMessage(Convert.ToBase64String(packet));
                        await SendEnvelopeAsync(socket, ControlMessageKind.ScreenFrameChunk, message, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Transient error sending frame: {exception.Message}");
                    await Task.Delay(50, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when session stops
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Screen capture stream ended: {exception.Message}");
        }
    }

    private async Task HandleAsync(ClientWebSocket socket, ControlEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Kind)
        {
            case ControlMessageKind.PeerReady:
                return;

            case ControlMessageKind.RtcOffer:
            {
                SessionAuthorization.Demand(grantedPermissions, SessionPermission.ViewScreen);
                if (_rtc is not null)
                {
                    var offer = ProtocolJson.Deserialize<RtcSessionDescriptionMessage>(envelope.PayloadJson);
                    ProtocolValidation.Validate(offer);
                    var answer = await _rtc.HandleOfferAndCreateAnswerAsync(offer);
                    await SendEnvelopeAsync(socket, ControlMessageKind.RtcAnswer, answer, cancellationToken);
                }
                return;
            }

            case ControlMessageKind.RtcIceCandidate:
            {
                if (_rtc is not null)
                {
                    var candidate = ProtocolJson.Deserialize<RtcIceCandidateMessage>(envelope.PayloadJson);
                    ProtocolValidation.Validate(candidate);
                    _rtc.AddIceCandidate(candidate);
                }
                return;
            }

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

    private async Task SendEnvelopeAsync<T>(
        ClientWebSocket socket,
        ControlMessageKind kind,
        T payload,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            await WebSocketProtocol.SendAsync(
                socket,
                new ControlEnvelope(kind, request.SessionId, sequence, ProtocolJson.Serialize(payload)),
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sessionCts?.Cancel();
        if (_rtc is not null)
        {
            await _rtc.DisposeAsync();
        }
        _sendLock.Dispose();
    }
}
