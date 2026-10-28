namespace UrlShortener.Shortener.Api.Models;

/// <summary>Payload for creating a short link.</summary>
public sealed record CreateShortUrlRequest
{
    /// <summary>
    /// Required. The address to redirect to: an absolute http or https URL of at most 2048 characters.
    /// (Checked by the service rather than by a [Required] attribute, so every error has the same snake_case shape.)
    /// </summary>
    public string? LongUrl { get; init; }

    /// <summary>
    /// Optional human-readable short code (3-50 letters, digits, '-' or '_'; case-sensitive).
    /// Leave out (or empty) to get a generated code.
    /// </summary>
    public string? CustomAlias { get; init; }

    /// <summary>
    /// Optional time when the link stops working. Must be in the future and at most about 20 years away.
    /// Defaults to one year from now.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    // The framework logs action arguments (at Debug/Trace level) using ToString. The long URL can carry
    // secrets in its query string, so never print the values.
    /// <inheritdoc />
    public override string ToString() => nameof(CreateShortUrlRequest);
}

/// <summary>A newly created short link.</summary>
/// <param name="ShortUrl">The link to share.</param>
/// <param name="LongUrl">The address it redirects to.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
/// <param name="CreatedAt">When the link was created.</param>
public sealed record ShortUrlResponse(string ShortUrl, string LongUrl, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

/// <summary>Click statistics of a short link.</summary>
/// <param name="ShortCode">The link's code.</param>
/// <param name="TotalClicks">Redirects counted so far; may lag real time by a few seconds.</param>
/// <param name="CreatedAt">When the link was created.</param>
/// <param name="LastAccessed">When the link was last followed, or null if it never was.</param>
public sealed record AnalyticsResponse(string ShortCode, long TotalClicks, DateTimeOffset CreatedAt, DateTimeOffset? LastAccessed);

/// <summary>One link in a user's list.</summary>
/// <param name="ShortCode">The link's code.</param>
/// <param name="LongUrl">The address it redirects to.</param>
/// <param name="CreatedAt">When the link was created.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record UserUrlItem(string ShortCode, string LongUrl, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>One page of a user's links, newest first.</summary>
/// <param name="Items">The links on this page.</param>
/// <param name="NextPageToken">Pass as page_token to get the next page; null on the last page.</param>
public sealed record UserUrlsResponse(IReadOnlyList<UserUrlItem> Items, string? NextPageToken);
