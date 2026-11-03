namespace UrlShortener.Core.Storage;

/// <summary>Clicks collected for one link since the last flush.</summary>
public sealed record BufferedClicks(long Count, DateTimeOffset LastAccessed);

/// <summary>
/// Collects clicks in fast storage so the redirect path never waits on the database.
/// The analytics worker periodically drains it into <see cref="IClickRepository"/>.
/// </summary>
public interface IClickBuffer
{
    /// <summary>Counts one click. Best-effort: failures are swallowed so a redirect is never blocked.</summary>
    Task RecordAsync(string shortCode, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Removes and returns up to <paramref name="max"/> short codes that have unflushed clicks.</summary>
    Task<IReadOnlyList<string>> PopChangedCodesAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>Atomically takes (reads and clears) the buffered clicks of one link; null if there are none.</summary>
    Task<BufferedClicks?> TakeAsync(string shortCode, CancellationToken cancellationToken = default);

    /// <summary>Puts clicks back after a failed flush so they are not lost.</summary>
    Task RestoreAsync(string shortCode, BufferedClicks clicks, CancellationToken cancellationToken = default);
}
