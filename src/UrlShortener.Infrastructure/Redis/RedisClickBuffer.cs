using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Telemetry;

namespace UrlShortener.Infrastructure.Redis;

/// <summary>
/// Buffers clicks in Redis. Per link it keeps a counter and a last-access time, and it keeps a set of
/// "changed" short codes so the worker knows which links to flush without scanning every key.
/// </summary>
public sealed class RedisClickBuffer(IConnectionMultiplexer redis, RedisKeys keys, ILogger<RedisClickBuffer> logger)
    : IClickBuffer
{
    private IDatabase Database => redis.GetDatabase();

    // During a Redis outage every redirect fails here; log it once in a while, not every time.
    private readonly ThrottledLogger _failures = new(logger, TimeSpan.FromSeconds(30));

    public async Task RecordAsync(string shortCode, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        using var span = UrlShortenerTelemetry.StartClientSpan("clicks.record", "redis");
        try
        {
            // Send the three commands together in one network round trip.
            var batch = Database.CreateBatch();
            var counted = batch.StringIncrementAsync(keys.Clicks(shortCode));
            var marked = batch.SetAddAsync(keys.ChangedCodes, shortCode);
            var timestamped = batch.StringSetAsync(keys.LastAccess(shortCode), at.ToUnixTimeMilliseconds());
            batch.Execute();

            await Task.WhenAll(counted, marked, timestamped).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Losing a click is better than slowing or failing a redirect.
            _failures.Warn(ex, "Could not record a click.");
        }
    }

    public async Task<IReadOnlyList<string>> PopChangedCodesAsync(int max, CancellationToken cancellationToken = default)
    {
        var codes = await Database.SetPopAsync(keys.ChangedCodes, max).WaitAsync(cancellationToken);
        return codes.Select(code => (string)code!).ToList();
    }

    public async Task<BufferedClicks?> TakeAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        // GETDEL reads and removes in one step, so a click arriving meanwhile starts a fresh counter.
        var count = Database.StringGetDeleteAsync(keys.Clicks(shortCode));
        var last = Database.StringGetDeleteAsync(keys.LastAccess(shortCode));
        await Task.WhenAll(count, last).WaitAsync(cancellationToken);

        if (!count.Result.HasValue)
        {
            return null;
        }

        // The timestamp is written together with the count, so it is only missing in rare races; "now" is
        // then within one flush interval of the truth.
        var lastAccessed = last.Result.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)last.Result)
            : DateTimeOffset.UtcNow;

        return new BufferedClicks((long)count.Result, lastAccessed);
    }

    public async Task RestoreAsync(string shortCode, BufferedClicks clicks, CancellationToken cancellationToken = default)
    {
        var batch = Database.CreateBatch();
        var counted = batch.StringIncrementAsync(keys.Clicks(shortCode), clicks.Count);
        var marked = batch.SetAddAsync(keys.ChangedCodes, shortCode);

        // Only restore the timestamp if no newer click has set one in the meantime.
        var timestamped = batch.StringSetAsync(
            keys.LastAccess(shortCode),
            clicks.LastAccessed.ToUnixTimeMilliseconds(),
            when: When.NotExists);
        batch.Execute();

        await Task.WhenAll(counted, marked, timestamped).WaitAsync(cancellationToken);
    }
}
