using System.Text.Json;
using RemoteDesk.Protocol;

namespace RemoteDesk.Client;

internal sealed class RecentSessionHistory(string? storagePath = null) : IDisposable
{
    private const int MaximumEntries = 50;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _storagePath = storagePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwiftDesk",
        "recent-sessions.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RecentSessionResponse>> LoadAsync(
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

    public async Task<IReadOnlyList<RecentSessionResponse>> MergeAndSaveAsync(
        IEnumerable<RecentSessionResponse> sessions,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = await LoadWithoutLockAsync(cancellationToken);
            var merged = Merge(cached, sessions, Math.Clamp(limit, 1, MaximumEntries));
            await SaveWithoutLockAsync(merged, cancellationToken);
            return merged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<RecentSessionResponse>> UpsertAsync(
        RecentSessionResponse session,
        CancellationToken cancellationToken = default) =>
        MergeAndSaveAsync([session], MaximumEntries, cancellationToken);

    internal static IReadOnlyList<RecentSessionResponse> Merge(
        IEnumerable<RecentSessionResponse> cached,
        IEnumerable<RecentSessionResponse> current,
        int limit)
    {
        // Current entries win because they contain the freshest server/session state.
        return cached
            .Concat(current)
            .GroupBy(session => session.SessionId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderByDescending(session => session.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaximumEntries))
            .ToArray();
    }

    private async Task<IReadOnlyList<RecentSessionResponse>> LoadWithoutLockAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<RecentSessionResponse[]>(
                       stream,
                       SerializerOptions,
                       cancellationToken)
                   ?? [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            // A damaged or temporarily locked cache must never prevent connections.
            return [];
        }
    }

    private async Task SaveWithoutLockAsync(
        IReadOnlyList<RecentSessionResponse> sessions,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storagePath)
            ?? throw new InvalidOperationException("Recent-session storage path has no directory.");
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
                    sessions.Take(MaximumEntries).ToArray(),
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
