using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.AnalyticsWorker.Services;
using UrlShortener.TestSupport;

namespace UrlShortener.AnalyticsWorker.Tests;

/// <summary>Tests the worker's liveness: a stuck background job must be noticed.</summary>
public class WorkerHeartbeatTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private TestTimeProvider _time = null!;
    private WorkerHeartbeat _heartbeat = null!;

    [SetUp]
    public void SetUp()
    {
        _time = new TestTimeProvider(Start);
        _heartbeat = new WorkerHeartbeat(_time);
    }

    [Test]
    public void ANewWorker_IsNotStalled()
    {
        Assert.That(_heartbeat.StalledJobs(), Is.Empty);
    }

    [Test]
    public void AJobThatKeepsBeating_IsNotStalled()
    {
        for (var i = 0; i < 20; i++)
        {
            _heartbeat.Beat("flush", TimeSpan.FromSeconds(10));
            _time.Now += TimeSpan.FromSeconds(10);
        }

        Assert.That(_heartbeat.StalledJobs(), Is.Empty);
    }

    [Test]
    public void AJobIsStalled_OnceItHasBeenSilentForMuchLongerThanItsInterval()
    {
        _heartbeat.Beat("flush", TimeSpan.FromSeconds(60));

        _time.Now += TimeSpan.FromSeconds(179);
        Assert.That(_heartbeat.StalledJobs(), Is.Empty, "three intervals are allowed");

        _time.Now += TimeSpan.FromSeconds(2);
        Assert.That(_heartbeat.StalledJobs(), Is.EqualTo(new[] { "flush" }));
    }

    [Test]
    public void AShortIntervalJob_GetsAtLeastThirtySecondsOfSilence()
    {
        _heartbeat.Beat("flush", TimeSpan.FromSeconds(2));

        _time.Now += TimeSpan.FromSeconds(29);
        Assert.That(_heartbeat.StalledJobs(), Is.Empty);

        _time.Now += TimeSpan.FromSeconds(2);
        Assert.That(_heartbeat.StalledJobs(), Is.EqualTo(new[] { "flush" }));
    }

    [Test]
    public void OnlyTheStuckJobIsReported()
    {
        _heartbeat.Beat("flush", TimeSpan.FromSeconds(10));
        _heartbeat.Beat("checkpoint", TimeSpan.FromSeconds(10));

        _time.Now += TimeSpan.FromSeconds(40);
        _heartbeat.Beat("checkpoint", TimeSpan.FromSeconds(10));

        Assert.That(_heartbeat.StalledJobs(), Is.EqualTo(new[] { "flush" }));
    }
}

/// <summary>Starts the real worker (with fake storage) and probes its health endpoints.</summary>
public class WorkerHealthEndpointTests
{
    private sealed class WorkerFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");

            // Nothing listens on these ports, so the readiness checks see Cassandra and Redis as down.
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cassandra:Port"] = "1",
                ["Cassandra:ConnectTimeoutMs"] = "200",
                ["Redis:ConnectionString"] = "localhost:1",
                ["Redis:ConnectTimeoutMs"] = "200",
                ["Redis:OperationTimeoutMs"] = "100",
                ["Worker:FlushInterval"] = "00:00:30",
                ["Worker:CheckpointInterval"] = "00:00:30",
            }));

            // The background jobs would only log outage warnings here; give them fakes so they are quiet.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UrlShortener.Core.Storage.IClickBuffer>();
                services.AddSingleton<UrlShortener.Core.Storage.IClickBuffer>(new FakeClickBuffer());
                services.RemoveAll<UrlShortener.Core.Storage.IClickRepository>();
                services.AddSingleton<UrlShortener.Core.Storage.IClickRepository>(new FakeClickRepository());
                services.RemoveAll<UrlShortener.Core.Storage.IIdCounter>();
                services.AddSingleton<UrlShortener.Core.Storage.IIdCounter>(new FakeIdCounter());
                services.RemoveAll<UrlShortener.Core.Storage.IIdCounterCheckpointStore>();
                services.AddSingleton<UrlShortener.Core.Storage.IIdCounterCheckpointStore>(new FakeCheckpointStore());
            });
        }
    }

    [Test]
    public async Task Live_IsHealthy_WhileTheBackgroundJobsRun()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(body.GetProperty("checks")[0].GetProperty("name").GetString(), Is.EqualTo("worker-loops"));
    }

    [Test]
    public async Task Ready_IsUnhealthy_WhenStorageCannotBeReached_AndDoesNotLeakDetails()
    {
        await using var factory = new WorkerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Unhealthy"));
        Assert.That(body.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()), Is.EquivalentTo(new[] { "cassandra", "redis" }));
        Assert.That(text, Does.Not.Contain("localhost").And.Not.Contain("Exception"));
    }
}
