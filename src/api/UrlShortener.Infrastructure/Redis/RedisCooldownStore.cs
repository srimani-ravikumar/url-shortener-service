using StackExchange.Redis;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Redis;

/// <summary>Cooldowns kept in Redis: SET ... NX with an expiry is an atomic "start unless running".</summary>
public sealed class RedisCooldownStore(IConnectionMultiplexer redis, RedisKeys keys) : ICooldownStore
{
    public async Task<bool> TryStartAsync(string key, TimeSpan duration, CancellationToken cancellationToken = default) =>
        await redis.GetDatabase().StringSetAsync(keys.Cooldown(key), 1, duration, When.NotExists).WaitAsync(cancellationToken);
}
