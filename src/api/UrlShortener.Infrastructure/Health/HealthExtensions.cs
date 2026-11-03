using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace UrlShortener.Infrastructure.Health;

/// <summary>Registers the health checks and the two probe endpoints every service exposes.</summary>
public static class HealthExtensions
{
    /// <summary>Tag of checks that decide whether the process is alive (restart it if they fail).</summary>
    public const string LiveTag = "live";

    /// <summary>Tag of checks that decide whether the service can do its job (route traffic to it only if they pass).</summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Adds the readiness checks for Cassandra and Redis.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisRequired">
    /// True if the service cannot work without Redis (a failure makes it Unhealthy). False if it can fall back
    /// to the database, like the redirect service: a failure only makes it Degraded, which still counts as ready.
    /// </param>
    public static IHealthChecksBuilder AddUrlShortenerHealthChecks(this IServiceCollection services, bool redisRequired = true) =>
        services.AddHealthChecks()
            .AddCheck<CassandraHealthCheck>("cassandra", HealthStatus.Unhealthy, [ReadyTag], TimeSpan.FromSeconds(3))
            .AddCheck<RedisHealthCheck>("redis", redisRequired ? HealthStatus.Unhealthy : HealthStatus.Degraded, [ReadyTag], TimeSpan.FromSeconds(2));

    /// <summary>
    /// Maps GET /health/live and GET /health/ready, open to everyone (probes carry no API key).
    /// Live runs only the checks tagged <see cref="LiveTag"/> (none for the APIs, so it just proves the process answers).
    /// Ready runs the checks tagged <see cref="ReadyTag"/>. 503 means unhealthy; degraded still answers 200.
    /// </summary>
    public static IEndpointRouteBuilder MapUrlShortenerHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LiveTag),
            ResponseWriter = WriteReport,
        }).AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteReport,
        }).AllowAnonymous();

        return endpoints;
    }

    // Names and states only. Exception messages can contain host names or internals, so they stay in the logs.
    private static Task WriteReport(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
            }),
        });
}
