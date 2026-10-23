using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using RemoteDesk.Protocol;

namespace RemoteDesk.Transport;

public static class WebSocketProtocol
{
    public const int MaxControlMessageBytes = 64 * 1024;

    public static Task SendAsync(
        WebSocket socket,
        ControlEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(envelope));
        if (bytes.Length > MaxControlMessageBytes)
        {
            throw new InvalidDataException("Control message exceeds the size limit.");
        }

        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public static async ValueTask<ControlEnvelope?> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var rented = ArrayPool<byte>.Shared.Rent(MaxControlMessageBytes);
        try
        {
            var total = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    rented.AsMemory(total, MaxControlMessageBytes - total),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Expected a text control message.");
                }

                total += result.Count;
                if (!result.EndOfMessage && total == MaxControlMessageBytes)
                {
                    throw new InvalidDataException("Control message exceeds the size limit.");
                }
            }
            while (!result.EndOfMessage);

            return ProtocolJson.Deserialize<ControlEnvelope>(Encoding.UTF8.GetString(rented, 0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}

