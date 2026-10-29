namespace UrlShortener.Core.Storage;

/// <summary>A short link as stored in the database.</summary>
public sealed record UrlMapping(
    string ShortCode,
    string LongUrl,
    string UserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsCustom);

/// <summary>One entry in a user's list of links.</summary>
public sealed record UserUrl(string ShortCode, string LongUrl, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>One page of results plus an opaque token for the next page (null on the last page).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextPageToken);

/// <summary>Thrown when a page token was not produced by this service or has been altered.</summary>
public sealed class InvalidPageTokenException(Exception? inner = null)
    : Exception("The page token is invalid.", inner);

/// <summary>Reads and writes short links in the database (source of truth).</summary>
public interface IUrlMappingRepository
{
    /// <summary>
    /// Stores the link only if the short code is still free. Returns false when the code is already taken.
    /// </summary>
    /// <param name="ttlSeconds">How long the database keeps the row; see ExpiryPolicy.GetRowTtlSeconds.</param>
    Task<bool> TryInsertAsync(UrlMapping mapping, int ttlSeconds, CancellationToken cancellationToken = default);

    /// <summary>Finds a link by short code, or null if it does not exist (or has been purged).</summary>
    Task<UrlMapping?> GetAsync(string shortCode, CancellationToken cancellationToken = default);

    /// <summary>Lists a user's links, newest first, one page at a time.</summary>
    /// <exception cref="InvalidPageTokenException"><paramref name="pageToken"/> is not a valid token.</exception>
    Task<PagedResult<UserUrl>> ListByUserAsync(
        string userId,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one link. Does nothing if it does not exist.</summary>
    Task DeleteAsync(string shortCode, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user's whole link list (the entries of <see cref="ListByUserAsync"/>), not the links themselves.</summary>
    Task DeleteUserListAsync(string userId, CancellationToken cancellationToken = default);
}
