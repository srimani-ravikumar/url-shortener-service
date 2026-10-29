using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Telemetry;

namespace UrlShortener.Infrastructure.Redis;

/// <summary>
/// Caches short links in Redis. Values are stored as "{expiresAtUnixMs}|{longUrl}": cheaper to parse than
/// JSON, and safe because only the first '|' is a separator (a URL may contain more).
/// </summary>
public sealed class RedisUrlCache(IConnectionMultiplexer redis, RedisKeys keys, ILogger<RedisUrlCache> logger) : IUrlCache
{
    private const char Separator = '|';

    // During a Redis outage every redirect fails here; log it once in a while, not every time.
    private readonly ThrottledLogger _failures = new(logger, TimeSpan.FromSeconds(30));

    public async Task<CachedUrl?> GetAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        using var span = UrlShortenerTelemetry.StartClientSpan("cache.get", "redis");
        try
        {
            string? raw = await redis.GetDatabase().StringGetAsync(keys.Url(shortCode)).WaitAsync(cancellationToken);
            return raw is null ? null : Parse(raw);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A cache is an optimisation: when it is down, behave as a miss and let the database answer.
            _failures.Warn(ex, "Redis cache read failed; treating as a miss.");
            return null;
        }
    }

    public async Task SetAsync(string shortCode, CachedUrl url, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        // Redis rejects a zero or negative expiry, and an expired link must not be cached anyway.
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            using var span = UrlShortenerTelemetry.StartClientSpan("cache.set", "redis");
            var value = $"{url.ExpiresAt.ToUnixTimeMilliseconds()}{Separator}{url.LongUrl}";
            await redis.GetDatabase().StringSetAsync(keys.Url(shortCode), value, ttl).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _failures.Warn(ex, "Redis cache write failed; continuing without caching.");
        }
    }

    public async Task RemoveAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        // Not swallowed like the other operations: if the entry survives, a deleted link keeps redirecting.
        using var span = UrlShortenerTelemetry.StartClientSpan("cache.remove", "redis");
        await redis.GetDatabase().KeyDeleteAsync(keys.Url(shortCode)).WaitAsync(cancellationToken);
    }

    // A value that cannot be parsed (e.g. written by another version) is treated as a miss, not an error.
    private static CachedUrl? Parse(string raw)
    {
        var separatorIndex = raw.IndexOf(Separator);
        if (separatorIndex < 0 || !long.TryParse(raw.AsSpan(0, separatorIndex), out var expiresAtMs))
        {
            return null;
        }

        return new CachedUrl(raw[(separatorIndex + 1)..], DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs));
    }
}
