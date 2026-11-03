using UrlShortener.AnalyticsWorker.Configuration;
using UrlShortener.AnalyticsWorker.Services;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Health;

namespace UrlShortener.AnalyticsWorker.Extensions;

/// <summary>Registers everything the analytics worker needs, so Program.cs stays short.</summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAnalyticsWorker(this IServiceCollection services, IConfiguration configuration)
    {
        // Bad configuration should stop the worker at startup, not misbehave later.
        services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddUrlShortenerInfrastructure(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerHeartbeat>();
        services.AddSingleton<ClickFlusher>();
        services.AddHostedService<ClickFlushService>();
        services.AddHostedService<CounterCheckpointService>();

        // Ready = can reach Cassandra and Redis. Live = the background jobs are still turning.
        services.AddUrlShortenerHealthChecks(redisRequired: true)
            .AddCheck<WorkerHeartbeatHealthCheck>("worker-loops", tags: [HealthExtensions.LiveTag]);

        return services;
    }
}
