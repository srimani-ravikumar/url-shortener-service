using System.Net;
using System.Net.Http.Json;
using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Tests API key authentication and the secure-by-default rules.</summary>
public class AuthenticationTests : ApiTestBase
{
    private static readonly object Body = new { LongUrl = "https://example.com/a" };

    [Test]
    public async Task ARequestWithoutAnApiKey_Gets401WithAProblemDocument()
    {
        using var client = Factory.CreateClient();

        var response = await client.Shorten(Body);
        var problem = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Is.EqualTo("ApiKey"));
        Assert.That(problem.GetProperty("status").GetInt32(), Is.EqualTo(401));
        Assert.That(Factory.Links.Stored, Is.Empty);
    }

    [Test]
    public async Task AnUnknownApiKey_Gets401()
    {
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-a-real-key");

        var response = await client.Shorten(Body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AnOversizedApiKey_Gets401()
    {
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", new string('k', 300));

        var response = await client.Shorten(Body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task RepeatedApiKeyHeaders_Get401()
    {
        var apiKey = Factory.Users.AddUser("user-1");
        using var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", [apiKey, apiKey]);

        var response = await client.Shorten(Body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase("/api/v1/analytics/abc")]
    [TestCase("/api/v1/users/user-1/urls")]
    public async Task EveryEndpointRequiresAnApiKey(string path)
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task AUserCannotActAsAnotherByPuttingTheirIdInTheRequest()
    {
        // user-2 owns the link; user-1 sends their own key but asks about user-2's data.
        using var owner = Factory.CreateClientFor("user-2");
        await owner.Shorten(new { LongUrl = "https://example.com/a", CustomAlias = "owned-by-two" });
        using var attacker = Factory.CreateClientFor("user-1");

        var analytics = await attacker.GetAsync("/api/v1/analytics/owned-by-two");
        var list = await attacker.GetAsync("/api/v1/users/user-2/urls");

        Assert.That(analytics.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task TheOpenApiDocument_IsNotExposedInProduction()
    {
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UnknownPaths_RequireAnApiKeyToo_SoRoutesCannotBeProbedAnonymously()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TheOpenApiDocument_IsPublicInDevelopment_AndDescribesEveryEndpoint()
    {
        Factory.EnvironmentName = "Development";
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var document = await response.ReadJson();
        var paths = document.GetProperty("paths");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(paths.TryGetProperty("/api/v1/shorten", out _), Is.True);
        Assert.That(paths.TryGetProperty("/api/v1/analytics/{shortCode}", out _), Is.True);
        Assert.That(paths.TryGetProperty("/api/v1/users/{userId}/urls", out _), Is.True);
    }

    [Test]
    public void TheServiceRefusesToStart_WithoutAFeistelSecret()
    {
        Factory.Settings.Remove("Shortener:FeistelSecret");

        Assert.That(() => Factory.CreateClient(), Throws.Exception);
    }

    [Test]
    public async Task OnStartup_ALostIdCounterIsMovedPastTheLastCheckpoint()
    {
        // Redis lost the counter, but Cassandra remembers it had reached 5,000.
        await Factory.Checkpoints.SaveAsync("short_code", 5_000);
        using var client = Factory.CreateClientFor("user-1");

        var response = await client.Shorten(Body);
        var created = await response.ReadJson();

        // Restart point: checkpoint 5,000 + the default safety gap of 100,000; the next id is one past it.
        Assert.That(created.GetProperty("short_url").GetString(), Does.EndWith(Factory.CodeGenerator.Generate(105_001)));
    }
}
