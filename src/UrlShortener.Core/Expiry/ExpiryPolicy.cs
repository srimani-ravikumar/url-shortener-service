namespace UrlShortener.Core.Expiry;

/// <summary>
/// Decides when a link expires and converts that into the two TTLs the system needs:
/// the database row TTL (so Cassandra deletes old rows) and the cache TTL (so Redis and CDNs
/// never keep serving a link after it has expired).
/// </summary>
public sealed class ExpiryPolicy
{
    private readonly ExpiryOptions _options;
    private readonly TimeProvider _time;

    /// <param name="options">Lifetime settings; inconsistent values throw <see cref="ArgumentException"/>.</param>
    /// <param name="timeProvider">Clock to use; defaults to the system clock (tests pass a fake one).</param>
    public ExpiryPolicy(ExpiryOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Fail fast at startup on bad configuration instead of producing wrong TTLs at runtime.
        if (options.DefaultTtl <= TimeSpan.Zero || options.DefaultCacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentException("DefaultTtl and DefaultCacheTtl must be positive.", nameof(options));
        }

        if (options.GracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentException("GracePeriod must not be negative.", nameof(options));
        }

        // A default-lifetime link must itself fit under the Cassandra TTL cap once the grace period is added.
        if (options.DefaultTtl > options.MaxRowTtl - options.GracePeriod)
        {
            throw new ArgumentException("DefaultTtl plus GracePeriod must not exceed MaxRowTtl.", nameof(options));
        }

        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    // The row TTL is lifetime + grace, so the lifetime itself is capped below MaxRowTtl.
    public TimeSpan MaxLifetime => _options.MaxRowTtl - _options.GracePeriod;

    /// <summary>
    /// Works out the expiry for a new link: the default when none was requested,
    /// otherwise the requested time if it is in the future and not beyond <see cref="MaxLifetime"/>.
    /// </summary>
    public ExpiryResult Resolve(DateTimeOffset? requested)
    {
        var now = _time.GetUtcNow();
        if (requested is null)
        {
            // No expiry requested: every link still expires, after the default lifetime.
            return ExpiryResult.Success(now + _options.DefaultTtl);
        }

        // Store everything in UTC so offsets sent by clients never matter downstream.
        var expiresAt = requested.Value.ToUniversalTime();
        if (expiresAt <= now)
        {
            return ExpiryResult.Fail("expires_at must be in the future.");
        }

        if (expiresAt - now > MaxLifetime)
        {
            return ExpiryResult.Fail($"expires_at must be within {(int)MaxLifetime.TotalDays} days from now.");
        }

        return ExpiryResult.Success(expiresAt);
    }

    /// <summary>True once the expiry time has been reached (the redirector answers 410 Gone).</summary>
    public bool IsExpired(DateTimeOffset expiresAt) => expiresAt <= _time.GetUtcNow();

    /// <summary>
    /// Seconds to pass as the Cassandra <c>USING TTL</c> when writing a link: time until expiry plus the
    /// grace period, rounded up. Never returns 0: in Cassandra TTL 0 means "no expiry".
    /// </summary>
    public int GetRowTtlSeconds(DateTimeOffset expiresAt)
    {
        var seconds = Math.Ceiling((expiresAt - _time.GetUtcNow() + _options.GracePeriod).TotalSeconds);

        // Clamp to [1, max]: 1 avoids the "TTL 0 = forever" trap, max stays within what Cassandra accepts.
        return (int)Math.Clamp(seconds, 1, _options.MaxRowTtl.TotalSeconds);
    }

    /// <summary>
    /// How long a redirect may be cached: the smaller of the time left and the cap (default: <see cref="ExpiryOptions.DefaultCacheTtl"/>).
    /// Returns <see cref="TimeSpan.Zero"/> for an already expired link, meaning "do not cache".
    /// </summary>
    public TimeSpan GetCacheTtl(DateTimeOffset expiresAt, TimeSpan? cap = null)
    {
        var remaining = expiresAt - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var limit = cap ?? _options.DefaultCacheTtl;
        return remaining < limit ? remaining : limit;
    }
}
