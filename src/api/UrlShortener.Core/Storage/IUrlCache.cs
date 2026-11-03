namespace UrlShortener.Core.Storage;

/// <summary>What the redirect path needs to know about a link; cached so most redirects skip the database.</summary>
public sealed record CachedUrl(string LongUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Best-effort cache of short links. If the cache is unavailable, reads return null and writes do nothing,
/// so callers fall back to the database.
/// </summary>
public interface IUrlCache
{
    Task<CachedUrl?> GetAsync(string shortCode, CancellationToken cancellationToken = default);

    /// <summary>Caches a link for <paramref name="ttl"/>; a zero or negative ttl caches nothing.</summary>
    Task SetAsync(string shortCode, CachedUrl url, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cached link. Unlike reads and writes this is NOT best-effort: it throws when the cache is
    /// unreachable, because a deleted link must stop redirecting.
    /// </summary>
    Task RemoveAsync(string shortCode, CancellationToken cancellationToken = default);
}
