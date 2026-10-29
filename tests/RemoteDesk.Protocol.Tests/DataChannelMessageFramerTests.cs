using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Transport;
using Xunit;

namespace RemoteDesk.Protocol.Tests;

public sealed class DataChannelMessageFramerTests
{
    [Fact]
    public void SmallMessage_PassesThroughWithoutChunking()
    {
        var framer = new DataChannelMessageFramer();
        var original = RandomNumberGenerator.GetBytes(1024);

        var packets = framer.Encode(original);

        var packet = Assert.Single(packets);
        Assert.Same(original, packet);
        Assert.True(framer.TryDecode(packet, out var decoded));
        Assert.Same(original, decoded);
    }

    [Fact]
    public void LargeMessage_ReassemblesOutOfOrderWithinDataChannelLimit()
    {
        var sender = new DataChannelMessageFramer();
        var receiver = new DataChannelMessageFramer();
        var original = RandomNumberGenerator.GetBytes(510_222);

        var packets = sender.Encode(original);

        Assert.True(packets.Count > 1);
        Assert.All(packets, packet => Assert.True(packet.Length < 262_144));

        byte[]? decoded = null;
        foreach (var packet in packets.Reverse())
        {
            if (receiver.TryDecode(packet, out var completed))
            {
                decoded = completed;
            }
        }

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void IncompleteLargeMessage_IsNotEmitted()
    {
        var sender = new DataChannelMessageFramer();
        var receiver = new DataChannelMessageFramer();
        var original = RandomNumberGenerator.GetBytes(510_222);
        var packets = sender.Encode(original);

        Assert.False(receiver.TryDecode(packets[0], out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void RelaySizedChunks_ReassembleLargeFrame()
    {
        var sender = new DataChannelMessageFramer();
        var receiver = new DataChannelMessageFramer();
        var original = RandomNumberGenerator.GetBytes(510_222);

        var packets = sender.Encode(original, 36 * 1024);

        Assert.All(packets, packet => Assert.True(packet.Length <= (36 * 1024) + 24));
        byte[]? decoded = null;
        foreach (var packet in packets)
        {
            if (receiver.TryDecode(packet, out var completed))
            {
                decoded = completed;
            }
        }

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void RelaySizedChunk_FitsWebSocketControlLimit()
    {
        var packet = RandomNumberGenerator.GetBytes((36 * 1024) + 24);
        var chunk = new ScreenFrameChunkMessage(Convert.ToBase64String(packet));
        var envelope = new ControlEnvelope(
            ControlMessageKind.ScreenFrameChunk,
            "ses_test",
            1,
            ProtocolJson.Serialize(chunk));

        var serializedLength = Encoding.UTF8.GetByteCount(ProtocolJson.Serialize(envelope));

        Assert.True(serializedLength < WebSocketProtocol.MaxControlMessageBytes);
    }
}
