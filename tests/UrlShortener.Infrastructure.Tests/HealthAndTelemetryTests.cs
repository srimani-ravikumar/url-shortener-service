using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure.Cassandra;
using UrlShortener.Infrastructure.Health;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the health checks and trace spans against the real Cassandra and Redis.</summary>
public class HealthAndTelemetryTests : IntegrationTestBase
{
    private static HealthCheckContext ContextFor(IHealthCheck check) =>
        new() { Registration = new HealthCheckRegistration("test", check, HealthStatus.Unhealthy, null) };

    [Test]
    public async Task Cassandra_IsHealthy_WhenItAnswers()
    {
        var check = new CassandraHealthCheck(Services.GetRequiredService<CassandraConnection>());

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
    }

    [Test]
    public async Task Redis_IsHealthy_WhenItAnswersAPing()
    {
        var check = new RedisHealthCheck(Services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>());

        var result = await check.CheckHealthAsync(ContextFor(check));

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
    }

    // Cassandra is real here; only Redis is pointed at a port nothing listens on.
    [TestCase(true, HealthStatus.Unhealthy)]
    [TestCase(false, HealthStatus.Degraded)]
    public async Task ARedisOutage_IsUnhealthyOrOnlyDegraded_DependingOnWhetherTheServiceNeedsRedis(bool redisRequired, HealthStatus expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:ConnectionString"] = "localhost:1",
                ["Redis:ConnectTimeoutMs"] = "200",
                ["Redis:OperationTimeoutMs"] = "100",
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddUrlShortenerInfrastructure(configuration);
        services.AddUrlShortenerHealthChecks(redisRequired);
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Tags.Contains(HealthExtensions.ReadyTag));

        Assert.That(report.Status, Is.EqualTo(expected));
        Assert.That(report.Entries["cassandra"].Status, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(report.Entries["redis"].Status, Is.EqualTo(expected));
    }

    [Test]
    public async Task ACassandraCall_ProducesASpan_WithTheQueryTextButNeverTheValues()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "UrlShortener",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { lock (spans) { spans.Add(span); } },
        };
        ActivitySource.AddActivityListener(listener);
        var code = NewShortCode();

        await Services.GetRequiredService<IUrlMappingRepository>().GetAsync(code);

        Activity span;
        lock (spans)
        {
            span = spans.Single(s => s.OperationName == "cassandra.execute");
        }

        Assert.That(span.Kind, Is.EqualTo(ActivityKind.Client));
        Assert.That(span.GetTagItem("db.system"), Is.EqualTo("cassandra"));
        Assert.That((string?)span.GetTagItem("db.statement"), Does.Contain("FROM url_mappings WHERE short_code = ?"));
        Assert.That(span.TagObjects.Select(tag => tag.Value?.ToString()), Has.None.Contains(code));
    }

    [Test]
    public async Task ARedisCall_ProducesASpan()
    {
        var names = new List<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "UrlShortener",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { lock (names) { names.Add(span.OperationName); } },
        };
        ActivitySource.AddActivityListener(listener);

        await Services.GetRequiredService<IUrlCache>().GetAsync("span-check");

        lock (names)
        {
            Assert.That(names, Does.Contain("cache.get"));
        }
    }
}
