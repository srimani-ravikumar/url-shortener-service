using System.Net;
using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Tests GET /api/v1/analytics/{shortCode} and GET /api/v1/users/{userId}/urls.</summary>
public class QueryEndpointTests : ApiTestBase
{
    private async Task<string> CreateLink(HttpClient client, string alias, TimeSpan? ageOffset = null)
    {
        // Moving the clock between creations gives the links distinct, ordered creation times.
        if (ageOffset is not null)
        {
            Factory.Time.Now += ageOffset.Value;
        }

        var response = await client.Shorten(new { LongUrl = $"https://example.com/{alias}", CustomAlias = alias });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        return alias;
    }

    [Test]
    public async Task Analytics_ReturnsTheClickStatisticsOfAnOwnedLink()
    {
        using var client = Factory.CreateClientFor("user-1");
        var code = await CreateLink(client, "my-link");
        var lastClick = Factory.Time.Now.AddMinutes(3);
        await Factory.Clicks.AddClicksAsync(code, 42, lastClick);

        var response = await client.GetAsync($"/api/v1/analytics/{code}");
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("short_code").GetString(), Is.EqualTo(code));
        Assert.That(body.GetProperty("total_clicks").GetInt64(), Is.EqualTo(42));
        Assert.That(body.GetProperty("created_at").GetDateTimeOffset(), Is.EqualTo(Factory.Time.Now));
        Assert.That(body.GetProperty("last_accessed").GetDateTimeOffset(), Is.EqualTo(lastClick));
    }

    [Test]
    public async Task Analytics_ShowsZeroClicksAndNoLastAccess_ForALinkNobodyFollowed()
    {
        using var client = Factory.CreateClientFor("user-1");
        var code = await CreateLink(client, "quiet-link");

        var body = await (await client.GetAsync($"/api/v1/analytics/{code}")).ReadJson();

        Assert.That(body.GetProperty("total_clicks").GetInt64(), Is.EqualTo(0));
        Assert.That(body.GetProperty("last_accessed").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
    }

    [Test]
    public async Task Analytics_Returns403_ForSomeoneElsesLink()
    {
        using var owner = Factory.CreateClientFor("user-1");
        using var other = Factory.CreateClientFor("user-2");
        var code = await CreateLink(owner, "private-link");

        var response = await other.GetAsync($"/api/v1/analytics/{code}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Analytics_Returns404_ForAnUnknownCode()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.GetAsync("/api/v1/analytics/does-not-exist");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
    }

    [Test]
    public async Task ListUrls_ReturnsTheUsersLinksNewestFirst_InPages()
    {
        using var client = Factory.CreateClientFor("user-1");
        using var other = Factory.CreateClientFor("user-2");
        await CreateLink(client, "link-a");
        await CreateLink(client, "link-b", TimeSpan.FromMinutes(1));
        await CreateLink(client, "link-c", TimeSpan.FromMinutes(1));
        await CreateLink(other, "not-mine", TimeSpan.FromMinutes(1));

        var firstPage = await (await client.GetAsync("/api/v1/users/user-1/urls?page_size=2")).ReadJson();
        var token = firstPage.GetProperty("next_page_token").GetString();
        var secondPage = await (await client.GetAsync($"/api/v1/users/user-1/urls?page_size=2&page_token={token}")).ReadJson();

        Assert.That(Codes(firstPage), Is.EqualTo(new[] { "link-c", "link-b" }));
        Assert.That(token, Is.Not.Null);
        Assert.That(Codes(secondPage), Is.EqualTo(new[] { "link-a" }));
        Assert.That(secondPage.GetProperty("next_page_token").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
    }

    [Test]
    public async Task ListUrls_DescribesEachLink()
    {
        using var client = Factory.CreateClientFor("user-1");
        await CreateLink(client, "link-a");

        var page = await (await client.GetAsync("/api/v1/users/user-1/urls")).ReadJson();
        var item = page.GetProperty("items")[0];

        Assert.That(item.GetProperty("short_code").GetString(), Is.EqualTo("link-a"));
        Assert.That(item.GetProperty("long_url").GetString(), Is.EqualTo("https://example.com/link-a"));
        Assert.That(item.GetProperty("created_at").GetDateTimeOffset(), Is.EqualTo(Factory.Time.Now));
        Assert.That(item.GetProperty("expires_at").GetDateTimeOffset(), Is.EqualTo(Factory.Time.Now.AddDays(365)));
    }

    [Test]
    public async Task ListUrls_ReturnsAnEmptyList_ForAUserWithoutLinks()
    {
        using var client = Factory.CreateClientFor("user-1");

        var page = await (await client.GetAsync("/api/v1/users/user-1/urls")).ReadJson();

        Assert.That(page.GetProperty("items").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task ListUrls_Returns403_ForAnotherUsersList()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.GetAsync("/api/v1/users/user-2/urls");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [TestCase("page_size=0")]
    [TestCase("page_size=101")]
    [TestCase("page_size=-5")]
    [TestCase("page_size=abc")]
    public async Task ListUrls_Returns400_ForAnInvalidPageSize(string query)
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.GetAsync($"/api/v1/users/user-1/urls?{query}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ListUrls_Returns400_ForATokenTheServiceDidNotIssue()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.GetAsync("/api/v1/users/user-1/urls?page_token=forged");
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(body.GetProperty("errors").TryGetProperty("page_token", out _), Is.True);
    }

    private static string[] Codes(System.Text.Json.JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("short_code").GetString()!).ToArray();
}
