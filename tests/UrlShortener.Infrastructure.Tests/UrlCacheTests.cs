using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the URL cache against a real Redis.</summary>
public class UrlCacheTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private IUrlCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = Services.GetRequiredService<IUrlCache>();

    [Test]
    public async Task Set_ThenGet_ReturnsTheSameLink()
    {
        // The URL contains '|', which is also the separator used inside the stored value.
        var url = new CachedUrl("https://example.com/a|b?x=1|2", Now.AddDays(1));

        await _cache.SetAsync("abc", url, TimeSpan.FromMinutes(5));

        Assert.That(await _cache.GetAsync("abc"), Is.EqualTo(url));
    }

    [Test]
    public async Task Get_ReturnsNull_ForAnUnknownCode()
    {
        Assert.That(await _cache.GetAsync("never-cached"), Is.Null);
    }

    [Test]
    public async Task Set_ExpiresTheEntryAfterTheTtl()
    {
        await _cache.SetAsync("short-lived", new CachedUrl("https://example.com", Now.AddDays(1)), TimeSpan.FromSeconds(1));
        Assert.That(await _cache.GetAsync("short-lived"), Is.Not.Null);

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.That(await _cache.GetAsync("short-lived"), Is.Null);
    }

    [Test]
    public async Task Set_UsesTheGivenTtlAsTheRedisExpiry()
    {
        await _cache.SetAsync("with-ttl", new CachedUrl("https://example.com", Now.AddDays(1)), TimeSpan.FromMinutes(10));

        var remaining = await Redis.KeyTimeToLiveAsync(KeyPrefix + "url:with-ttl");

        Assert.That(remaining, Is.GreaterThan(TimeSpan.FromMinutes(9)).And.LessThanOrEqualTo(TimeSpan.FromMinutes(10)));
    }

    [TestCase(0)]
    [TestCase(-30)]
    public async Task Set_CachesNothing_ForAZeroOrNegativeTtl(int seconds)
    {
        await _cache.SetAsync("expired", new CachedUrl("https://example.com", Now.AddDays(-1)), TimeSpan.FromSeconds(seconds));

        Assert.That(await Redis.KeyExistsAsync(KeyPrefix + "url:expired"), Is.False);
    }

    [TestCase("garbage")]
    [TestCase("not-a-number|https://example.com")]
    public async Task Get_TreatsAnUnreadableValueAsAMiss(string raw)
    {
        await Redis.StringSetAsync(KeyPrefix + "url:corrupt", raw);

        Assert.That(await _cache.GetAsync("corrupt"), Is.Null);
    }
}
