using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Cassandra;
using UrlShortener.Infrastructure.Redis;
using UrlShortener.Infrastructure.Web;

namespace UrlShortener.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Answers storage outages (Redis or Cassandra unavailable) with 503 + Retry-After and every other
    /// unexpected error with a generic 500, both as problem documents.
    /// </summary>
    public static IServiceCollection AddTransientFailureHandling(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.AddExceptionHandler<TransientFailureExceptionHandler>();
        return services;
    }

    /// <summary>
    /// Registers the Cassandra and Redis implementations of the storage interfaces.
    /// Settings come from the "Cassandra", "Redis" and "IdCounter" configuration sections.
    /// Nothing connects until first use, so every service can register everything and use only what it needs.
    /// </summary>
    public static IServiceCollection AddUrlShortenerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CassandraOptions>(configuration.GetSection(CassandraOptions.SectionName));
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
        services.Configure<IdCounterOptions>(configuration.GetSection(IdCounterOptions.SectionName));

        // Cassandra
        services.AddSingleton<CassandraConnection>();
        services.AddSingleton<IUrlMappingRepository, UrlMappingRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IClickRepository, ClickRepository>();
        services.AddSingleton<IEmailTokenStore, EmailTokenStore>();
        services.AddSingleton<IIdCounterCheckpointStore, IdCounterCheckpointStore>();

        // Redis
        services.AddSingleton(provider => new RedisKeys(provider.GetRequiredService<IOptions<RedisOptions>>().Value.KeyPrefix));
        services.AddSingleton<IConnectionMultiplexer>(provider =>
            ConnectionMultiplexer.Connect(CreateRedisConfiguration(provider.GetRequiredService<IOptions<RedisOptions>>().Value)));
        services.AddSingleton<IIdCounter, RedisIdCounter>();
        services.AddSingleton<IUrlCache, RedisUrlCache>();
        services.AddSingleton<IClickBuffer, RedisClickBuffer>();
        services.AddSingleton<ICooldownStore, RedisCooldownStore>();

        services.AddSingleton<IdCounterDurability>();

        return services;
    }

    public static ConfigurationOptions CreateRedisConfiguration(RedisOptions options)
    {
        var configuration = ConfigurationOptions.Parse(options.ConnectionString);

        // Start even when Redis is down and keep retrying in the background; redirects fall back to Cassandra.
        configuration.AbortOnConnectFail = false;

        // While disconnected, fail commands immediately instead of queueing them until they time out.
        // Otherwise every redirect would wait out the full timeout during a Redis outage.
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.ConnectTimeout = options.ConnectTimeoutMs;
        configuration.SyncTimeout = options.OperationTimeoutMs;
        configuration.AsyncTimeout = options.OperationTimeoutMs;
        return configuration;
    }
}
