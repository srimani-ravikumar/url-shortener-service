namespace UrlShortener.Core.Storage;

/// <summary>Click statistics for one short link. A link with no clicks has 0 clicks and no last access time.</summary>
public sealed record ClickStats(long TotalClicks, DateTimeOffset? LastAccessed);

/// <summary>Persistent click counts (the Redis buffer is flushed into these by the analytics worker).</summary>
public interface IClickRepository
{
    /// <summary>Adds <paramref name="count"/> clicks to a link and records when it was last accessed.</summary>
    Task AddClicksAsync(
        string shortCode,
        long count,
        DateTimeOffset lastAccessed,
        CancellationToken cancellationToken = default);

    Task<ClickStats> GetAsync(string shortCode, CancellationToken cancellationToken = default);
}
