using System.Net.WebSockets;
using System.Security.Cryptography;
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
    private long _sequence;

    public SessionPermission Permissions { get; } = permissions;

    public async Task AnnounceAsync(CancellationToken cancellationToken)
    {
        await SendEnvelopeAsync(
            ControlMessageKind.PeerReady,
            new PeerReadyMessage("controller", SessionRole.Controller),
            cancellationToken);
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

