using Microsoft.Extensions.Options;
using UrlShortener.Core.Expiry;
using UrlShortener.Infrastructure;
using UrlShortener.Redirector.Api.Configuration;
using UrlShortener.Redirector.Api.Services;

namespace UrlShortener.Redirector.Api.Extensions;

/// <summary>Registers everything the redirect service needs, so Program.cs stays short.</summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRedirectorApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RedirectorOptions>()
            .Bind(configuration.GetSection(RedirectorOptions.SectionName))
            .Validate(options => options.ClientCacheMaxAgeSeconds >= 0, "Redirector:ClientCacheMaxAgeSeconds must not be negative.")
            .ValidateOnStart();
        services.Configure<ExpiryOptions>(configuration.GetSection("Expiry"));

        services.AddUrlShortenerInfrastructure(configuration);
        services.AddTransientFailureHandling();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new ExpiryPolicy(
            provider.GetRequiredService<IOptions<ExpiryOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRedirectService, RedirectService>();

        return services;
    }
}
