using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Core.Storage;
using UrlShortener.TestSupport;

namespace UrlShortener.Redirector.Api.Tests;

/// <summary>
/// Runs the real redirect service in memory with fake storage. By default the cache and click buffer are fakes;
/// set <see cref="UseRealRedis"/> to keep the real Redis-backed ones, pointed at a port nothing listens on.
/// Change <see cref="Settings"/> or <see cref="UseRealRedis"/> before the first request.
/// </summary>
internal sealed class RedirectorApiFactory : WebApplicationFactory<Program>
{
    public FakeUrlMappingRepository Links { get; } = new();

    public FakeUrlCache Cache { get; } = new();

    public FakeClickBuffer Clicks { get; } = new();

    public TestTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    public bool UseRealRedis { get; set; }

    public Dictionary<string, string?> Settings { get; } = new()
    {
        // Nothing listens here, so a real Redis client behaves like Redis is down.
        ["Redis:ConnectionString"] = "localhost:1",
        ["Redis:ConnectTimeoutMs"] = "200",
        ["Redis:OperationTimeoutMs"] = "100",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureServices(services =>
        {
            Replace<IUrlMappingRepository>(services, Links);
            Replace<TimeProvider>(services, Time);
            if (!UseRealRedis)
            {
                Replace<IUrlCache>(services, Cache);
                Replace<IClickBuffer>(services, Clicks);
            }
        });
    }

    /// <summary>A client that shows redirects instead of following them.</summary>
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Stores a link that expires <paramref name="expiresIn"/> from the test clock's current time.</summary>
    public UrlMapping AddLink(string code, string longUrl, TimeSpan expiresIn)
    {
        var link = new UrlMapping(code, longUrl, "user-1", Time.Now, Time.Now + expiresIn, IsCustom: false);
        Links.Add(link);
        return link;
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }
}
