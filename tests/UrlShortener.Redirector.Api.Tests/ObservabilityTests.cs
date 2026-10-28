using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Core.Storage;
using UrlShortener.TestSupport;

namespace UrlShortener.Redirector.Api.Tests;

/// <summary>Tests health probes and metrics of the Redirector API.</summary>
public class ObservabilityTests
{
    private const string Destination = "https://example.com/page";

    private RedirectorApiFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new RedirectorApiFactory();

    [TearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    [Test]
    public async Task Live_IsHealthy_WithoutAnyCredentials()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Ready_IsUnhealthy_WhenCassandraCannotBeReached_AndRedisIsOnlyDegraded()
    {
        // Nothing listens on these ports. Redis down is survivable here, Cassandra down is not.
        _factory.Settings["Cassandra:Port"] = "1";
        _factory.Settings["Cassandra:ConnectTimeoutMs"] = "200";
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        var statuses = body.GetProperty("checks").EnumerateArray()
            .ToDictionary(check => check.GetProperty("name").GetString()!, check => check.GetProperty("status").GetString());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(statuses, Is.EqualTo(new Dictionary<string, string?> { ["cassandra"] = "Unhealthy", ["redis"] = "Degraded" }));
        Assert.That(text, Does.Not.Contain("localhost").And.Not.Contain("Exception"));
    }

    [Test]
    public async Task HealthEndpoints_AreNotMistakenForShortCodes()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.That(_factory.Cache.GetCalls, Is.EqualTo(0));
        Assert.That(_factory.Links.GetCalls, Is.EqualTo(0));
        Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString(), Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task Requests_AreCountedByResult()
    {
        using var metrics = new MetricCapture();
        _factory.AddLink("good", Destination, TimeSpan.FromDays(1));
        _factory.AddLink("old", Destination, TimeSpan.FromDays(-1));
        using var client = _factory.CreateNoRedirectClient();

        await client.GetAsync("/good");
        await client.GetAsync("/good");
        await client.GetAsync("/old");
        await client.GetAsync("/missing");
        await client.GetAsync("/favicon.ico");

        Assert.That(metrics.Sum("urlshortener.redirects", ("result", "found")), Is.EqualTo(2));
        Assert.That(metrics.Sum("urlshortener.redirects", ("result", "expired")), Is.EqualTo(1));
        Assert.That(metrics.Sum("urlshortener.redirects", ("result", "not_found")), Is.EqualTo(1));
        Assert.That(metrics.Sum("urlshortener.redirects", ("result", "invalid_code")), Is.EqualTo(1));
    }

    [Test]
    public async Task CacheLookups_AreCountedAsHitsAndMisses()
    {
        using var metrics = new MetricCapture();
        _factory.AddLink("good", Destination, TimeSpan.FromDays(1));
        using var client = _factory.CreateNoRedirectClient();

        await client.GetAsync("/good"); // miss: read from the database, then cached
        await client.GetAsync("/good"); // hit
        await client.GetAsync("/good"); // hit
        await client.GetAsync("/nope"); // miss

        Assert.That(metrics.Sum("urlshortener.cache.lookups", ("result", "hit")), Is.EqualTo(2));
        Assert.That(metrics.Sum("urlshortener.cache.lookups", ("result", "miss")), Is.EqualTo(2));
    }
}
