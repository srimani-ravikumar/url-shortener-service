using Microsoft.AspNetCore.Mvc;
using UrlShortener.Redirector.Api.Services;

namespace UrlShortener.Redirector.Api.Controllers;

/// <summary>Sends visitors of a short link to its destination. Public: no API key is needed.</summary>
[ApiController]
public sealed class RedirectController(IRedirectService redirects) : ControllerBase
{
    /// <summary>
    /// Redirects <paramref name="shortCode"/> to its long URL with 302, so every click comes back to the service
    /// (needed for click counting and for expiring or changing links). Answers 404 for an unknown code
    /// and 410 for an expired link.
    /// </summary>
    [HttpGet("{shortCode}", Name = "Redirect")]
    public async Task<IActionResult> Get(string shortCode, CancellationToken cancellationToken)
    {
        var result = await redirects.ResolveAsync(shortCode, cancellationToken);

        switch (result.Status)
        {
            case RedirectStatus.Found:
                Response.Headers.Location = result.LongUrl;
                Response.Headers.CacheControl = CacheControlFor(result.ClientCacheFor);
                return StatusCode(StatusCodes.Status302Found);

            case RedirectStatus.Expired:
                return Problem(
                    statusCode: StatusCodes.Status410Gone,
                    title: "Link expired",
                    detail: "This short link has expired.");

            default:
                return Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Link not found",
                    detail: "There is no link with that short code.");
        }
    }

    // Downstream caches may keep the redirect for a short while; "no-store" when there is nothing to cache.
    private static string CacheControlFor(TimeSpan cacheFor)
    {
        var seconds = (long)Math.Floor(cacheFor.TotalSeconds);
        return seconds > 0 ? $"public, max-age={seconds}" : "no-store";
    }
}
