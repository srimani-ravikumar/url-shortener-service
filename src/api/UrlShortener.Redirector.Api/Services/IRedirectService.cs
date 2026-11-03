namespace UrlShortener.Redirector.Api.Services;

/// <summary>How a request for a short code ended.</summary>
public enum RedirectStatus
{
    /// <summary>The link exists and is active: redirect to <see cref="RedirectResult.LongUrl"/>.</summary>
    Found,

    /// <summary>No such link (or the code cannot be one).</summary>
    NotFound,

    /// <summary>The link existed but its expiry time has passed.</summary>
    Expired,
}

/// <summary>Outcome of looking up a short code.</summary>
public sealed record RedirectResult(RedirectStatus Status, string? LongUrl = null, TimeSpan ClientCacheFor = default)
{
    public static RedirectResult NotFound { get; } = new(RedirectStatus.NotFound);

    public static RedirectResult Expired { get; } = new(RedirectStatus.Expired);

    /// <summary>A redirect that downstream caches may reuse for <paramref name="clientCacheFor"/>.</summary>
    public static RedirectResult Found(string longUrl, TimeSpan clientCacheFor) =>
        new(RedirectStatus.Found, longUrl, clientCacheFor);
}

/// <summary>Finds where a short code leads.</summary>
public interface IRedirectService
{
    /// <summary>
    /// Looks the code up (cache first, then the database). A successful lookup counts as a click.
    /// </summary>
    Task<RedirectResult> ResolveAsync(string shortCode, CancellationToken cancellationToken);
}
