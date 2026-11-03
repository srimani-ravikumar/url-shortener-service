namespace UrlShortener.Core.Expiry;

/// <summary>Settings for link lifetimes and the matching database and cache TTLs.</summary>
public sealed record ExpiryOptions
{
    // Applied when the caller does not send expires_at.
    public TimeSpan DefaultTtl { get; init; } = TimeSpan.FromDays(365);

    // Row outlives expires_at by this long so the redirector can answer 410 instead of 404.
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromDays(30);

    // Upper bound for how long a redirect is kept in Redis (or advertised via Cache-Control).
    public TimeSpan DefaultCacheTtl { get; init; } = TimeSpan.FromHours(24);

    // Cassandra's hard cap on a row TTL (20 years).
    public TimeSpan MaxRowTtl { get; init; } = TimeSpan.FromSeconds(630_720_000);
}
