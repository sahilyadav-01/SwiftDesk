using System.Security.Cryptography;
using RemoteDesk.Protocol;

namespace RemoteDesk.Transport;

public sealed class SafeFileReceiver : IAsyncDisposable
{
    private readonly FileTransferOffer _offer;
    private readonly string _temporaryPath;
    private readonly string _destinationPath;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _completed;
    private long _written;

    private SafeFileReceiver(
        FileTransferOffer offer,
        string temporaryPath,
        string destinationPath,
        FileStream stream)
    {
        _offer = offer;
        _temporaryPath = temporaryPath;
        _destinationPath = destinationPath;
        _stream = stream;
    }

    public long BytesWritten => _written;
    public long TotalBytes => _offer.FileSize;

    public static SafeFileReceiver Create(
        string approvedDestinationDirectory,
        FileTransferOffer offer,
        long maxFileBytes = ProtocolValidation.DefaultMaxFileBytes)
    {
        ProtocolValidation.Validate(offer, maxFileBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedDestinationDirectory);

        var root = Path.GetFullPath(approvedDestinationDirectory);
        Directory.CreateDirectory(root);

        var destinationPath = Path.GetFullPath(Path.Combine(root, offer.FileName));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Destination escaped the approved directory.");
        }

        var temporaryPath = Path.Combine(root, $".{offer.TransferId}.{Guid.NewGuid():N}.part");
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return new SafeFileReceiver(offer, temporaryPath, destinationPath, stream);
    }

    public async ValueTask WriteChunkAsync(
        long offset,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        if (offset != _written)
        {
            throw new InvalidDataException($"Expected byte offset {_written}, received {offset}.");
        }

        if (data.IsEmpty || _written + data.Length > _offer.FileSize)
        {
            throw new InvalidDataException("Chunk is empty or exceeds the declared file size.");
        }

        await _stream.WriteAsync(data, cancellationToken);
        _hash.AppendData(data.Span);
        _written += data.Length;
    }

    public async ValueTask<string> CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        if (_written != _offer.FileSize)
        {
            throw new InvalidDataException($"Expected {_offer.FileSize} bytes, received {_written}.");
        }

        await _stream.FlushAsync(cancellationToken);
        await _stream.DisposeAsync();

        var actualHash = Convert.ToHexString(_hash.GetHashAndReset());
        if (!string.Equals(actualHash, _offer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(_temporaryPath);
            _completed = true;
            throw new InvalidDataException("The transferred file failed its SHA-256 integrity check.");
        }

        File.Move(_temporaryPath, _destinationPath, overwrite: false);
        _completed = true;
        return _destinationPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            _hash.Dispose();
            return;
        }

        _completed = true;
        await _stream.DisposeAsync();
        _hash.Dispose();

        if (File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
    }
}

