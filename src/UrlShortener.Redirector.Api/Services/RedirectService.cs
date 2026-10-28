using System.Diagnostics;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Expiry;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Telemetry;
using UrlShortener.Redirector.Api.Configuration;

namespace UrlShortener.Redirector.Api.Services;

/// <inheritdoc />
internal sealed class RedirectService(
    IUrlCache cache,
    IUrlMappingRepository links,
    IClickBuffer clicks,
    ExpiryPolicy expiryPolicy,
    TimeProvider time,
    IOptions<RedirectorOptions> options,
    ILogger<RedirectService> logger) : IRedirectService
{
    private readonly TimeSpan _clientCacheCap = TimeSpan.FromSeconds(options.Value.ClientCacheMaxAgeSeconds);

    public async Task<RedirectResult> ResolveAsync(string shortCode, CancellationToken cancellationToken)
    {
        // Junk paths (favicon.ico, scanners) cannot be short codes: answer without any lookup.
        if (!ShortCodeFormat.IsValid(shortCode))
        {
            UrlShortenerTelemetry.RecordRedirect("invalid_code");
            return RedirectResult.NotFound;
        }

        var result = await LookUpAsync(shortCode, cancellationToken);

        UrlShortenerTelemetry.RecordRedirect(result.Status switch
        {
            RedirectStatus.Found => "found",
            RedirectStatus.Expired => "expired",
            _ => "not_found",
        });

        // Which link was asked for is useful in a trace, but would be far too many values for a metric label.
        Activity.Current?.SetTag("urlshortener.short_code", shortCode);
        return result;
    }

    private async Task<RedirectResult> LookUpAsync(string shortCode, CancellationToken cancellationToken)
    {
        // Layer 1: the cache. If it is down it just reports a miss.
        var cached = await cache.GetAsync(shortCode, cancellationToken);
        UrlShortenerTelemetry.RecordCacheLookup(hit: cached is not null);
        if (cached is not null)
        {
            return Decide(shortCode, cached.LongUrl, cached.ExpiresAt);
        }

        // Layer 2: the database, which is the source of truth.
        var link = await links.GetAsync(shortCode, cancellationToken);
        if (link is null)
        {
            return RedirectResult.NotFound;
        }

        var result = Decide(shortCode, link.LongUrl, link.ExpiresAt);
        if (result.Status == RedirectStatus.Found)
        {
            // The next request for this code can skip the database. The response does not wait for the write.
            var cacheFor = expiryPolicy.GetCacheTtl(link.ExpiresAt);
            BackgroundTask.Observe(
                cache.SetAsync(shortCode, new CachedUrl(link.LongUrl, link.ExpiresAt), cacheFor, CancellationToken.None),
                logger,
                $"cache {shortCode}");
        }

        return result;
    }

    // The same rules apply to a cached link and one read from the database.
    private RedirectResult Decide(string shortCode, string longUrl, DateTimeOffset expiresAt)
    {
        if (expiryPolicy.IsExpired(expiresAt))
        {
            return RedirectResult.Expired;
        }

        // Defence in depth: links are validated when created, but never redirect to a non-web address
        // (javascript:, file:, ...) even if storage somehow holds one.
        var location = ToLocation(longUrl);
        if (location is null)
        {
            logger.LogError("Link {ShortCode} holds a destination that is not a usable http(s) address; refusing to redirect.", shortCode);
            return RedirectResult.NotFound;
        }

        RecordClick(shortCode);
        return RedirectResult.Found(location, expiryPolicy.GetCacheTtl(expiresAt, _clientCacheCap));
    }

    private void RecordClick(string shortCode) =>
        BackgroundTask.Observe(clicks.RecordAsync(shortCode, time.GetUtcNow(), CancellationToken.None), logger, $"click {shortCode}");

    // Returns the value for the Location header, or null if the address must not be redirected to.
    private static string? ToLocation(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Plain printable ASCII can be used as it is (the common case, no allocation).
        if (url.AsSpan().IndexOfAnyExceptInRange('!', '~') < 0)
        {
            return url;
        }

        // Spaces, control and non-ASCII characters are not allowed in a header value (the server would fail
        // the request), so use the percent-encoded form of the same address.
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : null;
    }
}
