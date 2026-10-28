using System.Diagnostics;
using System.Net;
using UrlShortener.Core.Storage;

namespace UrlShortener.Redirector.Api.Tests;

/// <summary>Tests GET /{shortCode}: the redirect, caching, expiry, failures and click counting.</summary>
public class RedirectEndpointTests
{
    private const string Destination = "https://example.com/very/long/url?ref=test";

    private RedirectorApiFactory _factory = null!;
    private HttpClient? _client;

    [SetUp]
    public void SetUp()
    {
        _factory = new RedirectorApiFactory();
        _client = null; // NUnit reuses this object for every test, so forget the previous test's client
    }

    [TearDown]
    public async Task TearDown()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    // The client is created on demand so a test can still change the factory's settings first.
    private HttpClient Client => _client ??= _factory.CreateNoRedirectClient();

    [Test]
    public async Task Redirect_Returns302WithLocation_FromTheDatabase_AndCachesTheLink()
    {
        var link = _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo(Destination));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(1));

        // Cached for 24 hours (the default cap), well short of the link's own lifetime.
        var cached = _factory.Cache.Entries["abc"];
        Assert.That(cached.Url, Is.EqualTo(new CachedUrl(Destination, link.ExpiresAt)));
        Assert.That(cached.Ttl, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public async Task Redirect_UsesTheCache_WithoutTouchingTheDatabase()
    {
        _factory.Cache.Entries["abc"] = (new CachedUrl(Destination, _factory.Time.Now.AddDays(1)), TimeSpan.FromHours(1));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo(Destination));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Redirect_ServesTheSecondRequestFromTheCache()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(30));

        await Client.GetAsync("/abc");
        var second = await Client.GetAsync("/abc");

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(1));
        Assert.That(_factory.Cache.GetCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task Redirect_Returns404_ForAnUnknownCode()
    {
        var response = await Client.GetAsync("/nope");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(_factory.Clicks.Clicks, Is.Empty);
        Assert.That(_factory.Cache.Entries, Is.Empty);
    }

    [Test]
    public async Task Redirect_Returns410_ForAnExpiredLink_AndDoesNotCacheOrCountIt()
    {
        // Past its expiry but the row is still stored (the database keeps it for a grace period).
        _factory.AddLink("old", Destination, TimeSpan.FromMinutes(-1));

        var response = await Client.GetAsync("/old");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(_factory.Clicks.Clicks, Is.Empty);
        Assert.That(_factory.Cache.Entries, Is.Empty);
    }

    [Test]
    public async Task Redirect_Returns410_ForACacheEntryThatHasExpired()
    {
        _factory.Cache.Entries["old"] = (new CachedUrl(Destination, _factory.Time.Now.AddSeconds(-1)), TimeSpan.FromHours(1));

        var response = await Client.GetAsync("/old");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Redirect_StopsWorking_TheMomentTheLinkExpires_EvenIfItIsStillCached()
    {
        _factory.AddLink("short", Destination, TimeSpan.FromMinutes(10));
        var before = await Client.GetAsync("/short");

        _factory.Time.Now += TimeSpan.FromMinutes(11);
        var after = await Client.GetAsync("/short");

        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
    }

    [Test]
    public async Task Redirect_AllowsDownstreamCachingForAShortTime_ByDefault()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=60"));
    }

    [Test]
    public async Task Redirect_NeverAllowsCachingPastTheLinksExpiry()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromSeconds(30));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=30"));
        Assert.That(_factory.Cache.Entries["abc"].Ttl, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public async Task Redirect_HonoursTheConfiguredClientCacheLimit()
    {
        _factory.Settings["Redirector:ClientCacheMaxAgeSeconds"] = "10";
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("public, max-age=10"));
    }

    [Test]
    public async Task Redirect_ForbidsDownstreamCaching_WhenTheLimitIsZero()
    {
        _factory.Settings["Redirector:ClientCacheMaxAgeSeconds"] = "0";
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.Headers.CacheControl?.ToString(), Is.EqualTo("no-store"));
    }

    [Test]
    public async Task Redirect_DoesNotRevealTheInstance_ByDefault()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.Headers.Contains("X-Served-By"), Is.False);
    }

    [Test]
    public async Task Redirect_NamesTheInstance_WhenEnabled_OnEveryResponse()
    {
        _factory.Settings["Redirector:ExposeInstanceId"] = "true";
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(365));

        var found = await Client.GetAsync("/abc");
        var missing = await Client.GetAsync("/zzz");

        Assert.That(found.Headers.GetValues("X-Served-By"), Is.EqualTo(new[] { Environment.MachineName }));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(missing.Headers.GetValues("X-Served-By"), Is.EqualTo(new[] { Environment.MachineName }));
    }

    [TestCase("/favicon.ico")]
    [TestCase("/robots.txt")]
    [TestCase("/has.dot")]
    [TestCase("/ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz")] // longer than any code
    public async Task Redirect_Returns404_ForPathsThatCannotBeShortCodes_WithoutAnyLookup(string path)
    {
        var response = await Client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(_factory.Cache.GetCalls, Is.EqualTo(0));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Redirect_TreatsCodesAsCaseSensitive()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(1));

        var response = await Client.GetAsync("/ABC");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Redirect_CountsOnlySuccessfulRedirects()
    {
        _factory.AddLink("good", Destination, TimeSpan.FromDays(1));
        _factory.AddLink("old", Destination, TimeSpan.FromDays(-1));

        await Client.GetAsync("/good");
        await Client.GetAsync("/good");
        await Client.GetAsync("/old");
        await Client.GetAsync("/missing");

        Assert.That(_factory.Clicks.Clicks.Select(click => click.ShortCode), Is.EqualTo(new[] { "good", "good" }));
        Assert.That(_factory.Clicks.Clicks.First().At, Is.EqualTo(_factory.Time.Now));
    }

    [Test]
    public async Task Redirect_StillWorks_WhenTheCacheIsUnavailable()
    {
        _factory.Cache.Unavailable = true;
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(1));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(_factory.Cache.Entries, Is.Empty);
    }

    [Test]
    public async Task Redirect_StillWorks_WhenCountingTheClickFails()
    {
        _factory.Clicks.FailWith = new InvalidOperationException("buffer exploded");
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(1));

        var response = await Client.GetAsync("/abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
    }

    [Test]
    public async Task Redirect_StaysFast_WhenRedisIsReallyDown()
    {
        // The real Redis-backed cache and click buffer, pointed at a port nothing listens on.
        _factory.UseRealRedis = true;
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(1));
        await Client.GetAsync("/abc"); // the first call pays for the failed connection attempt

        var stopwatch = Stopwatch.StartNew();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            statuses.Add((await Client.GetAsync("/abc")).StatusCode);
        }

        stopwatch.Stop();

        Assert.That(statuses, Has.All.EqualTo(HttpStatusCode.Found));

        // If each request waited for Redis to time out (100 ms) this would take 2 seconds or more.
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Redirect_Returns503WithRetryAfter_WhenTheDatabaseIsUnavailable()
    {
        _factory.Links.FailWith = new TimeoutException("cassandra timed out");

        var response = await Client.GetAsync("/abc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(_factory.Clicks.Clicks, Is.Empty);
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("file:///c:/windows/win.ini")]
    [TestCase("ftp://example.com/file")]
    public async Task Redirect_RefusesADestinationThatIsNotAWebAddress(string poisoned)
    {
        _factory.AddLink("bad", poisoned, TimeSpan.FromDays(1));

        var response = await Client.GetAsync("/bad");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Headers.Location, Is.Null);
        Assert.That(_factory.Clicks.Clicks, Is.Empty);
    }

    [TestCase("https://example.com/a b", "https://example.com/a%20b")]
    [TestCase("https://example.com/\u30d1\u30b9", "https://example.com/%E3%83%91%E3%82%B9")]
    public async Task Redirect_PercentEncodesCharactersThatCannotGoInAHeader(string stored, string expectedLocation)
    {
        _factory.AddLink("enc", stored, TimeSpan.FromDays(1));

        var response = await Client.GetAsync("/enc");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
        Assert.That(response.Headers.Location?.OriginalString, Is.EqualTo(expectedLocation));
    }

    [Test]
    public async Task Redirect_OnlyAcceptsGet()
    {
        _factory.AddLink("abc", Destination, TimeSpan.FromDays(1));

        var response = await Client.PostAsync("/abc", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
    }
}
