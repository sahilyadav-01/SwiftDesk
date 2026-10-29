using System.Buffers.Binary;

namespace RemoteDesk.Transport;

public sealed class DataChannelMessageFramer
{
    private const uint ChunkMagic = 0x53444643;
    private const int HeaderLength = 24;
    private const int MaximumChunkPayload = 240 * 1024;
    private const int MaximumMessageLength = 16 * 1024 * 1024;
    private const int MaximumPendingMessages = 8;
    private static readonly TimeSpan PendingMessageLifetime = TimeSpan.FromSeconds(5);

    private readonly object _syncRoot = new();
    private readonly Dictionary<long, PendingMessage> _pendingMessages = [];
    private long _nextMessageId;

    public IReadOnlyList<byte[]> Encode(byte[] message) => Encode(message, MaximumChunkPayload);

    public IReadOnlyList<byte[]> Encode(byte[] message, int maximumChunkPayload)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (maximumChunkPayload is <= 0 or > MaximumChunkPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChunkPayload));
        }

        if (message.Length <= maximumChunkPayload)
        {
            return [message];
        }

        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"Data channel message exceeds the {MaximumMessageLength}-byte safety limit.");
        }

        var messageId = Interlocked.Increment(ref _nextMessageId);
        var chunkCount = (message.Length + maximumChunkPayload - 1) / maximumChunkPayload;
        var chunks = new byte[chunkCount][];
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var sourceOffset = chunkIndex * maximumChunkPayload;
            var payloadLength = Math.Min(maximumChunkPayload, message.Length - sourceOffset);
            var packet = new byte[HeaderLength + payloadLength];
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), ChunkMagic);
            BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(4, 8), messageId);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), chunkIndex);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(16, 4), chunkCount);
            BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20, 4), message.Length);
            message.AsSpan(sourceOffset, payloadLength).CopyTo(packet.AsSpan(HeaderLength));
            chunks[chunkIndex] = packet;
        }

        return chunks;
    }

    public bool TryDecode(byte[] packet, out byte[]? message)
    {
        ArgumentNullException.ThrowIfNull(packet);
        message = null;
        if (packet.Length < HeaderLength
            || BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4)) != ChunkMagic)
        {
            message = packet;
            return true;
        }

        var messageId = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(4, 8));
        var chunkIndex = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(12, 4));
        var chunkCount = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(16, 4));
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(20, 4));
        var maximumChunkCount = (MaximumMessageLength + MaximumChunkPayload - 1) / MaximumChunkPayload;
        if (messageId <= 0
            || messageLength <= MaximumChunkPayload
            || messageLength > MaximumMessageLength
            || chunkCount <= 1
            || chunkCount > maximumChunkCount
            || chunkIndex < 0
            || chunkIndex >= chunkCount)
        {
            return false;
        }

        var payload = packet.AsSpan(HeaderLength).ToArray();
        lock (_syncRoot)
        {
            RemoveExpiredMessages();
            if (!_pendingMessages.TryGetValue(messageId, out var pending))
            {
                if (_pendingMessages.Count >= MaximumPendingMessages)
                {
                    var oldestMessageId = _pendingMessages.MinBy(entry => entry.Value.CreatedAt).Key;
                    _pendingMessages.Remove(oldestMessageId);
                }

                pending = new PendingMessage(messageLength, chunkCount, DateTimeOffset.UtcNow);
                _pendingMessages.Add(messageId, pending);
            }

            if (pending.MessageLength != messageLength || pending.Chunks.Length != chunkCount)
            {
                _pendingMessages.Remove(messageId);
                return false;
            }

            if (pending.Chunks[chunkIndex] is null)
            {
                pending.Chunks[chunkIndex] = payload;
                pending.ReceivedChunkCount++;
            }

            if (pending.ReceivedChunkCount != chunkCount)
            {
                return false;
            }

            var assembled = new byte[messageLength];
            var destinationOffset = 0;
            foreach (var chunk in pending.Chunks)
            {
                if (chunk is null || destinationOffset + chunk.Length > assembled.Length)
                {
                    _pendingMessages.Remove(messageId);
                    return false;
                }

                chunk.CopyTo(assembled, destinationOffset);
                destinationOffset += chunk.Length;
            }

            _pendingMessages.Remove(messageId);
            if (destinationOffset != assembled.Length)
            {
                return false;
            }

            message = assembled;
            return true;
        }
    }

    private void RemoveExpiredMessages()
    {
        var cutoff = DateTimeOffset.UtcNow - PendingMessageLifetime;
        foreach (var messageId in _pendingMessages
                     .Where(entry => entry.Value.CreatedAt < cutoff)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _pendingMessages.Remove(messageId);
        }
    }

    private sealed class PendingMessage(int messageLength, int chunkCount, DateTimeOffset createdAt)
    {
        public int MessageLength { get; } = messageLength;
        public byte[]?[] Chunks { get; } = new byte[chunkCount][];
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public int ReceivedChunkCount { get; set; }
    }
}
