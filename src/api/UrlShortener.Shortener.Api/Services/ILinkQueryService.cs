using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Services;

/// <summary>How a read request ended.</summary>
public enum QueryStatus
{
    /// <summary>The query succeeded and the result holds the data.</summary>
    Ok,

    /// <summary>The requested link does not exist.</summary>
    NotFound,

    /// <summary>The caller is authenticated but the data belongs to someone else.</summary>
    Forbidden,

    /// <summary>The page token was not issued by this service.</summary>
    InvalidPageToken,
}

/// <summary>Outcome of a read request: a status, plus the data when the status is Ok.</summary>
public sealed record QueryResult<T>(QueryStatus Status, T? Value = default)
{
    /// <summary>A successful result carrying the data.</summary>
    public static QueryResult<T> Ok(T value) => new(QueryStatus.Ok, value);

    /// <summary>The requested data does not exist.</summary>
    public static QueryResult<T> NotFound { get; } = new(QueryStatus.NotFound);

    /// <summary>The data exists but belongs to another user.</summary>
    public static QueryResult<T> Forbidden { get; } = new(QueryStatus.Forbidden);

    /// <summary>The page token was invalid.</summary>
    public static QueryResult<T> InvalidPageToken { get; } = new(QueryStatus.InvalidPageToken);
}

/// <summary>Click statistics together with the link's creation time.</summary>
public sealed record LinkAnalytics(string ShortCode, ClickStats Clicks, DateTimeOffset CreatedAt);

/// <summary>Read-only queries about a user's own links. Callers may only see their own data.</summary>
public interface ILinkQueryService
{
    /// <summary>Statistics for one link; NotFound if it does not exist, Forbidden if another user owns it.</summary>
    Task<QueryResult<LinkAnalytics>> GetAnalyticsAsync(string callerUserId, string shortCode, CancellationToken cancellationToken);

    /// <summary>A page of the user's links, newest first; Forbidden unless the caller is that user.</summary>
    Task<QueryResult<PagedResult<UserUrl>>> ListUserUrlsAsync(
        string callerUserId,
        string userId,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken);
}
