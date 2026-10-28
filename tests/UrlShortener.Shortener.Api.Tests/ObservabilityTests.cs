using System.Net;
using System.Text.Json;
using UrlShortener.TestSupport;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Tests health probes, metrics and logging of the Shortener API.</summary>
public class ObservabilityTests : ApiTestBase
{
    private const string LongUrl = "https://example.com/page";

    [Test]
    public async Task Live_IsHealthy_WithoutAnApiKey()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        var body = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task Ready_IsUnhealthy_WhenStorageCannotBeReached_AndDoesNotLeakDetails()
    {
        // Nothing listens on these ports, so Cassandra and Redis look down.
        Factory.Settings["Cassandra:Port"] = "1";
        Factory.Settings["Cassandra:ConnectTimeoutMs"] = "200";
        Factory.Settings["Redis:ConnectionString"] = "localhost:1";
        Factory.Settings["Redis:ConnectTimeoutMs"] = "200";
        Factory.Settings["Redis:OperationTimeoutMs"] = "100";
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Unhealthy"));
        Assert.That(
            body.GetProperty("checks").EnumerateArray().ToDictionary(check => check.GetProperty("name").GetString()!, check => check.GetProperty("status").GetString()),
            Is.EqualTo(new Dictionary<string, string?> { ["cassandra"] = "Unhealthy", ["redis"] = "Unhealthy" }));
        Assert.That(text, Does.Not.Contain("localhost").And.Not.Contain("Exception"));
    }

    [Test]
    public async Task CreatingLinks_IsCountedByOutcome()
    {
        using var metrics = new MetricCapture();
        using var client = Factory.CreateClientFor("user-1");

        await client.Shorten(new { LongUrl });
        await client.Shorten(new { LongUrl, CustomAlias = "taken-alias" });
        await client.Shorten(new { LongUrl, CustomAlias = "taken-alias" });
        await client.Shorten(new { LongUrl = "not a url" });

        Assert.That(metrics.Sum("urlshortener.shorten.requests", ("outcome", "created")), Is.EqualTo(2));
        Assert.That(metrics.Sum("urlshortener.shorten.requests", ("outcome", "alias_taken")), Is.EqualTo(1));
        Assert.That(metrics.Sum("urlshortener.shorten.requests", ("outcome", "invalid")), Is.EqualTo(1));
    }

    [Test]
    public async Task Logs_NeverContainTheApiKeyOrTheLongUrl()
    {
        // Log everything, including the framework's own request logging.
        Factory.Settings["Logging:LogLevel:Default"] = "Trace";
        Factory.Settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
        using var client = Factory.CreateClientFor("user-1");
        const string secretUrl = "https://example.com/reset?token=SECRET-TOKEN-123";

        await client.Shorten(new { LongUrl = secretUrl, CustomAlias = "log-check" });
        await client.Shorten(new { LongUrl = secretUrl, CustomAlias = "log-check" }); // 409
        await client.Shorten(new { LongUrl = secretUrl + " with space", CustomAlias = "x" }); // 400
        await client.GetAsync("/api/v1/analytics/log-check");

        Assert.That(Factory.Logs.Entries, Is.Not.Empty);
        AssertNeverLogged("SECRET-TOKEN-123");
        AssertNeverLogged("example.com/reset");
        foreach (var apiKey in Factory.IssuedApiKeys)
        {
            AssertNeverLogged(apiKey);
        }
    }

    // Lists the offending log lines (category and start of the message) so a leak is easy to find.
    private void AssertNeverLogged(string text)
    {
        var leaks = Factory.Logs.Entries
            .Where(entry => $"{entry.Message} {string.Join(' ', entry.State.Values)} {string.Join(' ', entry.Scopes.Values)} {entry.Exception}".Contains(text))
            .Select(entry => $"{entry.Category}: {entry.Message[..Math.Min(entry.Message.Length, 120)]}")
            .ToList();

        Assert.That(leaks, Is.Empty, $"'{text}' was logged");
    }

    [Test]
    public async Task CreatingALink_IsLoggedWithTheShortCodeAndTheUsersId()
    {
        using var client = Factory.CreateClientFor("user-42");

        await client.Shorten(new { LongUrl, CustomAlias = "audit-me" });

        var entry = Factory.Logs.Entries.Single(log => log.Message.StartsWith("Created short link"));
        Assert.That(entry.State["ShortCode"], Is.EqualTo("audit-me"));
        Assert.That(entry.Scopes["user_id"], Is.EqualTo("user-42"));
    }
}
