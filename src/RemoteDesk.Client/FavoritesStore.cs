using System.Text.Json;

namespace RemoteDesk.Client;

public sealed record FavoriteDevice(
    string DeviceId,
    string? Nickname = null,
    DateTimeOffset AddedAt = default);

internal sealed class FavoritesStore(string? storagePath = null) : IDisposable
{
    private const int MaximumEntries = 50;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _storagePath = storagePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwiftDesk",
        "favorites.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<FavoriteDevice>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadWithoutLockAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FavoriteDevice>> AddOrUpdateAsync(
        string deviceId,
        string? nickname = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return await LoadAsync(cancellationToken);
        }

        var normalizedId = deviceId.Trim();
        var normalizedNickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = (await LoadWithoutLockAsync(cancellationToken)).ToList();
            var existingIndex = cached.FindIndex(f => string.Equals(f.DeviceId, normalizedId, StringComparison.OrdinalIgnoreCase));
            
            var item = new FavoriteDevice(
                normalizedId,
                normalizedNickname,
                DateTimeOffset.UtcNow);

            if (existingIndex >= 0)
            {
                cached[existingIndex] = item;
            }
            else
            {
                cached.Insert(0, item);
            }

            var result = cached.Take(MaximumEntries).ToArray();
            await SaveWithoutLockAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<FavoriteDevice>> RemoveAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return await LoadAsync(cancellationToken);
        }

        var normalizedId = deviceId.Trim();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = (await LoadWithoutLockAsync(cancellationToken)).ToList();
            cached.RemoveAll(f => string.Equals(f.DeviceId, normalizedId, StringComparison.OrdinalIgnoreCase));
            var result = cached.ToArray();
            await SaveWithoutLockAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<FavoriteDevice>> LoadWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<FavoriteDevice[]>(
                       stream,
                       SerializerOptions,
                       cancellationToken)
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return [];
        }
    }

    private async Task SaveWithoutLockAsync(
        IReadOnlyList<FavoriteDevice> items,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storagePath)
            ?? throw new InvalidOperationException("Favorites storage path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    items.Take(MaximumEntries).ToArray(),
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

