using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Redis;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>
/// Checks behaviour when Redis is down. It points at a port nothing listens on, so these tests
/// need no running stack. Cache and click buffering must fail soft; the ID counter must fail loudly.
/// </summary>
public class RedisUnavailableTests
{
    private ConnectionMultiplexer _redis = null!;
    private RedisKeys _keys = null!;

    [OneTimeSetUp]
    public void ConnectToNothing()
    {
        var options = new RedisOptions { ConnectionString = "localhost:1", ConnectTimeoutMs = 200, OperationTimeoutMs = 200 };
        _redis = ConnectionMultiplexer.Connect(ServiceCollectionExtensions.CreateRedisConfiguration(options));
        _keys = new RedisKeys(string.Empty);
    }

    [OneTimeTearDown]
    public void Disconnect() => _redis.Dispose();

    [Test]
    public async Task Cache_Get_BehavesAsAMiss()
    {
        var cache = new RedisUrlCache(_redis, _keys, NullLogger<RedisUrlCache>.Instance);

        Assert.That(await cache.GetAsync("abc"), Is.Null);
    }

    [Test]
    public async Task Cache_Set_DoesNotThrow()
    {
        var cache = new RedisUrlCache(_redis, _keys, NullLogger<RedisUrlCache>.Instance);

        await cache.SetAsync("abc", new CachedUrl("https://example.com", DateTimeOffset.UtcNow.AddDays(1)), TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task ClickBuffer_Record_DoesNotThrow()
    {
        var buffer = new RedisClickBuffer(_redis, _keys, NullLogger<RedisClickBuffer>.Instance);

        await buffer.RecordAsync("abc", DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task IdCounter_Next_Throws_BecauseAnIdMustNeverBeGuessed()
    {
        var counter = new RedisIdCounter(_redis, _keys);

        await Assert.ThatAsync(
            () => counter.NextAsync(),
            Throws.InstanceOf<RedisException>().Or.InstanceOf<TimeoutException>());
    }
}
