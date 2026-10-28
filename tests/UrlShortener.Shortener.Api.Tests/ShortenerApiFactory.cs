using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Storage;
using UrlShortener.TestSupport;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>
/// Runs the real API in memory with fake storage, so tests exercise routing, authentication, validation,
/// rate limiting and error handling exactly as in production, without Cassandra or Redis.
/// Change <see cref="Settings"/> or <see cref="EnvironmentName"/> before the first request.
/// </summary>
internal sealed class ShortenerApiFactory : WebApplicationFactory<Program>
{
    public const string FeistelSecret = "test-feistel-secret-0123456789";
    public const string ShortUrlBase = "http://short.test";

    public FakeUrlMappingRepository Links { get; } = new();

    public FakeUserRepository Users { get; } = new();

    public FakeClickRepository Clicks { get; } = new();

    public FakeIdCounter Counter { get; } = new();

    public FakeCheckpointStore Checkpoints { get; } = new();

    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Everything the application logs while it runs.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>The API keys handed out by <see cref="CreateClientFor"/>.</summary>
    public List<string> IssuedApiKeys { get; } = [];

    /// <summary>The environment to run in. Production by default, so error responses are what real callers see.</summary>
    public string EnvironmentName { get; set; } = "Production";

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Shortener:ShortUrlBase"] = ShortUrlBase,
        ["Shortener:FeistelSecret"] = FeistelSecret,
    };

    /// <summary>Generates the codes the API should produce for counter values (same secret as the API under test).</summary>
    public ShortCodeGenerator CodeGenerator { get; } = new(FeistelObfuscator.FromSecret(FeistelSecret));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.Replace<IUrlMappingRepository>(Links);
            services.Replace<IUserRepository>(Users);
            services.Replace<IClickRepository>(Clicks);
            services.Replace<IIdCounter>(Counter);
            services.Replace<IIdCounterCheckpointStore>(Checkpoints);
            services.Replace<TimeProvider>(Time);
        });
    }

    /// <summary>Creates a user and a client that sends that user's API key.</summary>
    public HttpClient CreateClientFor(string userId)
    {
        var client = CreateClient();
        var apiKey = Users.AddUser(userId);
        IssuedApiKeys.Add(apiKey);
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void Replace<T>(this IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }
}

/// <summary>Helpers for talking JSON the way the API does (snake_case).</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>POSTs <paramref name="body"/> (an object, serialized as snake_case JSON) to the create endpoint.</summary>
    public static Task<HttpResponseMessage> Shorten(this HttpClient client, object body) =>
        client.PostAsJsonAsync("/api/v1/shorten", body, Options);

    public static async Task<JsonElement> ReadJson(this HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
