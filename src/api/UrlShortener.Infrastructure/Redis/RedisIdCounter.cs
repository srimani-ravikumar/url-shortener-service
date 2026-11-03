using StackExchange.Redis;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Telemetry;

namespace UrlShortener.Infrastructure.Redis;

/// <summary>The ID counter, stored in Redis. INCR is atomic, so no two callers ever get the same number.</summary>
public sealed class RedisIdCounter(IConnectionMultiplexer redis, RedisKeys keys) : IIdCounter
{
    // Runs inside Redis as one atomic step, so two services starting together cannot both reseed.
    // KEYS[1] = counter, ARGV[1] = checkpoint, ARGV[2] = value to reseed to.
    private const string ReseedScript = """
        local current = tonumber(redis.call('GET', KEYS[1])) or 0
        if current < tonumber(ARGV[1]) then
          redis.call('SET', KEYS[1], ARGV[2])
          return 1
        end
        return 0
        """;

    private IDatabase Database => redis.GetDatabase();

    public async Task<long> NextAsync(CancellationToken cancellationToken = default)
    {
        using var span = UrlShortenerTelemetry.StartClientSpan("id.next", "redis");
        return await Database.StringIncrementAsync(keys.IdCounter).WaitAsync(cancellationToken);
    }

    public async Task<long?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var value = await Database.StringGetAsync(keys.IdCounter).WaitAsync(cancellationToken);
        return value.HasValue ? (long)value : null;
    }

    public async Task<bool> ReseedIfBehindAsync(long checkpoint, long safetyGap, CancellationToken cancellationToken = default)
    {
        var result = await Database.ScriptEvaluateAsync(
            ReseedScript,
            [keys.IdCounter],
            [checkpoint, checkpoint + safetyGap]).WaitAsync(cancellationToken);

        return (int)result == 1;
    }
}
