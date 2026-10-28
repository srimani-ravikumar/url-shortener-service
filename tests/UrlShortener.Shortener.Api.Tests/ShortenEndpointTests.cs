using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Tests POST /api/v1/shorten.</summary>
public class ShortenEndpointTests : ApiTestBase
{
    private const string LongUrl = "https://example.com/very/long/url?ref=test";

    [Test]
    public async Task Create_ReturnsTheShortLink_WithLocationHeader()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl });
        var body = await response.ReadJson();

        var expectedUrl = $"{ShortenerApiFactory.ShortUrlBase}/{Factory.CodeGenerator.Generate(1)}";
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(response.Headers.Location?.ToString(), Is.EqualTo(expectedUrl));
        Assert.That(body.GetProperty("short_url").GetString(), Is.EqualTo(expectedUrl));
        Assert.That(body.GetProperty("long_url").GetString(), Is.EqualTo(LongUrl));
        Assert.That(body.GetProperty("created_at").GetDateTimeOffset(), Is.EqualTo(Factory.Time.Now));
    }

    [Test]
    public async Task Create_StoresTheLinkForTheCaller_WithTheDefaultOneYearExpiry()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl });
        var body = await response.ReadJson();

        var stored = Factory.Links.Stored.Values.Single();
        Assert.That(stored.Link.UserId, Is.EqualTo("user-1"));
        Assert.That(stored.Link.IsCustom, Is.False);
        Assert.That(stored.Link.ExpiresAt, Is.EqualTo(Factory.Time.Now.AddDays(365)));
        Assert.That(body.GetProperty("expires_at").GetDateTimeOffset(), Is.EqualTo(Factory.Time.Now.AddDays(365)));

        // The row is kept for the lifetime plus the 30-day grace period.
        Assert.That(stored.TtlSeconds, Is.EqualTo((365 + 30) * 24 * 3600));
    }

    [Test]
    public async Task Create_HonoursARequestedExpiry()
    {
        using var client = Factory.CreateClientFor("user-1");
        var expiresAt = Factory.Time.Now.AddHours(2);

        var response = await client.Shorten(new { LongUrl, ExpiresAt = expiresAt });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body.GetProperty("expires_at").GetDateTimeOffset(), Is.EqualTo(expiresAt));
        Assert.That(Factory.Links.Stored.Values.Single().TtlSeconds, Is.EqualTo((2 * 3600) + (30 * 24 * 3600)));
    }

    [Test]
    public async Task Create_UsesACustomAlias_WithoutConsumingACounterValue()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl, CustomAlias = "summer-sale" });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body.GetProperty("short_url").GetString(), Is.EqualTo($"{ShortenerApiFactory.ShortUrlBase}/summer-sale"));
        Assert.That(Factory.Links.Stored["summer-sale"].Link.IsCustom, Is.True);
        Assert.That(await Factory.Counter.GetCurrentAsync(), Is.Null);
    }

    [Test]
    public async Task Create_TreatsAnEmptyAliasAsNone()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl, CustomAlias = string.Empty });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(Factory.Links.Stored.Values.Single().Link.IsCustom, Is.False);
    }

    [Test]
    public async Task Create_Returns409_WhenTheAliasIsTaken()
    {
        using var client = Factory.CreateClientFor("user-1");
        using var otherClient = Factory.CreateClientFor("user-2");
        await client.Shorten(new { LongUrl, CustomAlias = "summer-sale" });

        var response = await otherClient.Shorten(new { LongUrl = "https://example.com/other", CustomAlias = "summer-sale" });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(body.GetProperty("status").GetInt32(), Is.EqualTo(409));
        Assert.That(Factory.Links.Stored["summer-sale"].Link.UserId, Is.EqualTo("user-1"));
    }

    [Test]
    public async Task Create_TreatsAliasesAsCaseSensitive()
    {
        using var client = Factory.CreateClientFor("user-1");

        var first = await client.Shorten(new { LongUrl, CustomAlias = "Summer-Sale" });
        var second = await client.Shorten(new { LongUrl, CustomAlias = "summer-sale" });

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    [TestCase("", "long_url")]
    [TestCase("not a url", "long_url")]
    [TestCase("example.com/no-scheme", "long_url")]
    [TestCase("ftp://example.com/file", "long_url")]
    [TestCase("javascript:alert(1)", "long_url")]
    [TestCase("http://short.test/abc", "long_url")] // points back at the short link service
    public async Task Create_Returns400_ForAnInvalidLongUrl(string longUrl, string field)
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl = longUrl });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty(field, out _), Is.True, body.ToString());
        Assert.That(Factory.Links.Stored, Is.Empty);
    }

    [TestCase("{}")]
    [TestCase("{\"long_url\": null}")]
    [TestCase("{\"long_url\": 42}")]
    [TestCase("not json at all")]
    public async Task Create_Returns400_ForABodyWithoutAUsableLongUrl(string json)
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.PostAsync("/api/v1/shorten", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase("ab")] // too short
    [TestCase("has space")]
    [TestCase("admin")] // reserved
    [TestCase("Admin")] // reserved, ignoring case
    public async Task Create_Returns400_ForAnInvalidAlias(string alias)
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl, CustomAlias = alias });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty("custom_alias", out _), Is.True, body.ToString());
    }

    [Test]
    public async Task Create_Returns400_ForAnExpiryInThePast()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl, ExpiresAt = Factory.Time.Now.AddMinutes(-1) });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty("expires_at", out _), Is.True, body.ToString());
    }

    [Test]
    public async Task Create_Returns400_ForAnExpiryBeyondTheMaximum()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(new { LongUrl, ExpiresAt = Factory.Time.Now.AddYears(21) });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_IgnoresAUserIdInTheBody_TheOwnerIsAlwaysTheCaller()
    {
        using var client = Factory.CreateClientFor("user-1");

        await client.Shorten(new { LongUrl, UserId = "someone-else" });

        Assert.That(Factory.Links.Stored.Values.Single().Link.UserId, Is.EqualTo("user-1"));
    }

    [Test]
    public async Task Create_TriesTheNextNumber_WhenAGeneratedCodeIsAlreadyTaken()
    {
        using var client = Factory.CreateClientFor("user-1");
        await TakeGeneratedCode(1);

        var response = await client.Shorten(new { LongUrl });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(body.GetProperty("short_url").GetString(), Does.EndWith(Factory.CodeGenerator.Generate(2)));
        Assert.That(await Factory.Counter.GetCurrentAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task Create_Returns503_WhenEveryGeneratedCodeIsTaken()
    {
        using var client = Factory.CreateClientFor("user-1");
        await TakeGeneratedCode(1);
        await TakeGeneratedCode(2);
        await TakeGeneratedCode(3);

        var response = await client.Shorten(new { LongUrl });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter, Is.Not.Null);
    }

    [Test]
    public async Task Create_Returns503WithRetryAfter_WhenStorageIsUnavailable()
    {
        using var client = Factory.CreateClientFor("user-1");
        Factory.Links.FailWith = new TimeoutException("cassandra timed out");

        var response = await client.Shorten(new { LongUrl });
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(body.GetProperty("status").GetInt32(), Is.EqualTo(503));
        Assert.That(body.ToString(), Does.Not.Contain("cassandra timed out"));
    }

    [Test]
    public async Task Create_Returns500WithoutLeakingDetails_ForAnUnexpectedError()
    {
        using var client = Factory.CreateClientFor("user-1");
        Factory.Links.FailWith = new InvalidOperationException("secret internal detail");

        var response = await client.Shorten(new { LongUrl });
        var text = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(text, Does.Not.Contain("secret internal detail"));
    }

    [Test]
    public async Task Create_Returns429_WhenOneUserCreatesLinksTooFast_WithoutAffectingOthers()
    {
        // A bucket of 3 that effectively never refills during the test.
        Factory.Settings["RateLimit:TokenLimit"] = "3";
        Factory.Settings["RateLimit:TokensPerPeriod"] = "1";
        Factory.Settings["RateLimit:ReplenishmentPeriodSeconds"] = "3600";
        using var busyClient = Factory.CreateClientFor("busy-user");
        using var otherClient = Factory.CreateClientFor("other-user");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await busyClient.Shorten(new { LongUrl })).StatusCode);
        }

        var rejected = await busyClient.Shorten(new { LongUrl });
        var otherUser = await otherClient.Shorten(new { LongUrl });

        Assert.That(statuses, Is.EqualTo(new[] { HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests }));
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(rejected.Headers.RetryAfter, Is.Not.Null);
        Assert.That(rejected.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(otherUser.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    // Occupies the short code the API would generate for a counter value, to force a collision.
    private async Task TakeGeneratedCode(long counterValue)
    {
        var code = Factory.CodeGenerator.Generate(counterValue);
        var now = Factory.Time.Now;
        await Factory.Links.TryInsertAsync(new UrlMapping(code, "https://example.com/taken", "someone", now, now.AddDays(1), IsCustom: false), 100);
    }
}
