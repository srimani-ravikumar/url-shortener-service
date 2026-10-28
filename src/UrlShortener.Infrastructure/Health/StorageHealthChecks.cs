using Cassandra;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry;
using StackExchange.Redis;
using UrlShortener.Infrastructure.Cassandra;

namespace UrlShortener.Infrastructure.Health;

/// <summary>Healthy if a trivial query on Cassandra succeeds.</summary>
public sealed class CassandraHealthCheck(CassandraConnection connection) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Probes run constantly; keep them out of the traces.
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            var cassandra = await connection.GetContextAsync(cancellationToken);
            await cassandra.ExecuteAsync(new SimpleStatement("SELECT release_version FROM system.local"), cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The registration decides how bad this is (Unhealthy, or Degraded when the service can cope without it).
            return new HealthCheckResult(context.Registration.FailureStatus, "Cassandra is not reachable.", ex);
        }
    }
}

/// <summary>Healthy if Redis answers a PING.</summary>
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = SuppressInstrumentationScope.Begin();

        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis is not reachable.", ex);
        }
    }
}
