using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using RemoteDesk.Core;
using RemoteDesk.Protocol;
using RemoteDesk.Transport;

namespace RemoteDesk.Client;

internal sealed class ClientSession(
    string sessionId,
    SessionPermission permissions,
    ClientWebSocket socket) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly DataChannelMessageFramer _relayFrameFramer = new();
    private readonly Channel<InputCommand> _pointerMoves = Channel.CreateBounded<InputCommand>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private long _sequence;
    private RtcPeerCoordinator? _rtc;
    private CancellationTokenSource? _sessionCts;
    private Task? _receivePumpTask;
    private Task? _pointerMovePumpTask;

    public SessionPermission Permissions { get; } = permissions;

    public event Action<byte[]>? OnFrameReceived;
    public event Action<string>? OnStatusMessage;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pointerMovePumpTask = Task.Run(
            () => SendPointerMovesAsync(_sessionCts.Token),
            _sessionCts.Token);

        await AnnounceAsync(_sessionCts.Token);

        if (Permissions.HasFlag(SessionPermission.ViewScreen))
        {
            _rtc = new RtcPeerCoordinator();
            _rtc.OnBinaryDataReceived += bytes => OnFrameReceived?.Invoke(bytes);
            _rtc.OnLocalIceCandidate += async candidate =>
            {
                try
                {
                    await SendEnvelopeAsync(ControlMessageKind.RtcIceCandidate, candidate, _sessionCts.Token);
                }
                catch
                {
                    // Ignore transient network errors during ICE exchange
                }
            };
            _rtc.OnConnectionStateChange += state => OnStatusMessage?.Invoke($"WebRTC P2P: {state}");

            await _rtc.InitializeDataChannelAsync("swiftdesk-stream");
            var offer = await _rtc.CreateOfferAsync();
            await SendEnvelopeAsync(ControlMessageKind.RtcOffer, offer, _sessionCts.Token);
        }

        _receivePumpTask = Task.Run(() => ReceivePumpLoopAsync(_sessionCts.Token), _sessionCts.Token);
    }

    private async Task ReceivePumpLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var envelope = await WebSocketProtocol.ReceiveAsync(socket, cancellationToken);
                if (envelope is null)
                {
                    break;
                }

                switch (envelope.Kind)
                {
                    case ControlMessageKind.RtcAnswer:
                    {
                        if (_rtc is not null)
                        {
                            var answer = ProtocolJson.Deserialize<RtcSessionDescriptionMessage>(envelope.PayloadJson);
                            ProtocolValidation.Validate(answer);
                            _rtc.HandleAnswer(answer);
                        }
                        break;
                    }

                    case ControlMessageKind.RtcIceCandidate:
                    {
                        if (_rtc is not null)
                        {
                            var candidate = ProtocolJson.Deserialize<RtcIceCandidateMessage>(envelope.PayloadJson);
                            ProtocolValidation.Validate(candidate);
                            _rtc.AddIceCandidate(candidate);
                        }
                        break;
                    }

                    case ControlMessageKind.ScreenFrame:
                    {
                        var frameMsg = ProtocolJson.Deserialize<ScreenFrameMessage>(envelope.PayloadJson);
                        var bytes = Convert.FromBase64String(frameMsg.Base64Data);
                        OnFrameReceived?.Invoke(bytes);
                        break;
                    }

                    case ControlMessageKind.ScreenFrameChunk:
                    {
                        var chunkMessage = ProtocolJson.Deserialize<ScreenFrameChunkMessage>(envelope.PayloadJson);
                        var packet = Convert.FromBase64String(chunkMessage.Base64Packet);
                        if (packet.Length > 40 * 1024)
                        {
                            throw new InvalidDataException("Relayed screen chunk exceeds the size limit.");
                        }

                        if (_relayFrameFramer.TryDecode(packet, out var frameBytes)
                            && frameBytes is { Length: > 0 })
                        {
                            OnFrameReceived?.Invoke(frameBytes);
                        }

                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during session shutdown
        }
        catch (Exception ex)
        {
            OnStatusMessage?.Invoke($"Session pump disconnected: {ex.Message}");
        }
    }

    public async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        await SendEnvelopeAsync(
            ControlMessageKind.PeerReady,
            new PeerReadyMessage("controller", SessionRole.Controller),
            cancellationToken);
    }

    public async Task SendInputAsync(
        RemoteInputKind kind,
        double normalizedX,
        double normalizedY,
        string? key = null,
        int wheelDelta = 0,
        CancellationToken cancellationToken = default)
    {
        if (!Permissions.HasFlag(SessionPermission.ControlInput))
        {
            return;
        }

        var cmd = new InputCommand(kind, normalizedX, normalizedY, key, wheelDelta);
        ProtocolValidation.Validate(cmd);

        if (kind == RemoteInputKind.PointerMove)
        {
            // Pointer events can arrive hundreds of times per second. Keep only the
            // newest position so reliable WebSocket delivery cannot build seconds of
            // stale cursor movement ahead of clicks and keystrokes.
            _pointerMoves.Writer.TryWrite(cmd);
            return;
        }

        await SendEnvelopeAsync(ControlMessageKind.Input, cmd, cancellationToken);
    }

    private async Task SendPointerMovesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _pointerMoves.Reader.WaitToReadAsync(cancellationToken))
            {
                InputCommand? latest = null;
                while (_pointerMoves.Reader.TryRead(out var pending))
                {
                    latest = pending;
                }

                if (latest is not null)
                {
                    await SendEnvelopeAsync(ControlMessageKind.Input, latest, cancellationToken);
                }

                // 30-35 Hz is visually smooth while keeping control traffic well
                // below the rate that can saturate a relayed WebSocket connection.
                await Task.Delay(30, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during session shutdown.
        }
    }

    public async Task SendFileAsync(string path, CancellationToken cancellationToken)
    {
        SessionAuthorization.Demand(Permissions, SessionPermission.TransferFiles);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Selected file was not found.", path);
        }

        ProtocolValidation.RequireSafeFileName(file.Name);
        if (file.Length > ProtocolValidation.DefaultMaxFileBytes)
        {
            throw new InvalidDataException("Selected file exceeds the configured transfer limit.");
        }

        string sha256;
        await using (var hashStream = file.OpenRead())
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
        }

        var transferId = $"tx_{Guid.NewGuid():N}";
        await SendEnvelopeAsync(
            ControlMessageKind.FileOffer,
            new FileTransferOffer(transferId, file.Name, file.Length, sha256),
            cancellationToken);

        var buffer = new byte[32 * 1024];
        long offset = 0;
        await using var stream = file.OpenRead();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await SendEnvelopeAsync(
                ControlMessageKind.FileChunk,
                new FileChunkMessage(transferId, offset, Convert.ToBase64String(buffer, 0, read)),
                cancellationToken);
            offset += read;
        }

        await SendEnvelopeAsync(
            ControlMessageKind.FileComplete,
            new FileTransferComplete(transferId),
            cancellationToken);
    }

    private async Task SendEnvelopeAsync<T>(
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
                new ControlEnvelope(kind, sessionId, sequence, ProtocolJson.Serialize(payload)),
                cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pointerMoves.Writer.TryComplete();
        _sessionCts?.Cancel();
        if (_pointerMovePumpTask is not null)
        {
            await _pointerMovePumpTask;
        }

        if (_receivePumpTask is not null)
        {
            try
            {
                await _receivePumpTask;
            }
            catch (OperationCanceledException) { }
        }

        if (_rtc is not null)
        {
            await _rtc.DisposeAsync();
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Client closed",
                CancellationToken.None);
        }

        socket.Dispose();
        _sendLock.Dispose();
    }
}
