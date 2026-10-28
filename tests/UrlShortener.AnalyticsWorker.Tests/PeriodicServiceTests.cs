using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Configuration;
using UrlShortener.AnalyticsWorker.Services;
using UrlShortener.Infrastructure;
using UrlShortener.TestSupport;

namespace UrlShortener.AnalyticsWorker.Tests;

/// <summary>Tests the background jobs: they run on a timer, survive outages and stop cleanly.</summary>
public class PeriodicServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private FakeClickBuffer _buffer = null!;
    private FakeClickRepository _repository = null!;
    private FakeIdCounter _counter = null!;
    private FakeCheckpointStore _checkpoints = null!;
    private IOptions<WorkerOptions> _options = null!;

    [SetUp]
    public void SetUp()
    {
        _buffer = new FakeClickBuffer();
        _repository = new FakeClickRepository();
        _counter = new FakeIdCounter();
        _checkpoints = new FakeCheckpointStore();
        _options = Options.Create(new WorkerOptions
        {
            FlushInterval = TimeSpan.FromMilliseconds(20),
            CheckpointInterval = TimeSpan.FromMilliseconds(20),
        });
    }

    private ClickFlushService CreateFlushService(WorkerHeartbeat? heartbeat = null) => new(
        new ClickFlusher(_buffer, _repository, _options, NullLogger<ClickFlusher>.Instance),
        _options,
        heartbeat ?? new WorkerHeartbeat(TimeProvider.System),
        NullLogger<ClickFlushService>.Instance);

    private CounterCheckpointService CreateCheckpointService() => new(
        new IdCounterDurability(_counter, _checkpoints, Options.Create(new IdCounterOptions()), NullLogger<IdCounterDurability>.Instance),
        _options,
        new WorkerHeartbeat(TimeProvider.System),
        NullLogger<CounterCheckpointService>.Instance);

    [Test]
    public async Task TheFlushService_WritesClicksOnItsOwn_AndStopsCleanly()
    {
        await _buffer.RecordAsync("a", Start);
        await _buffer.RecordAsync("a", Start.AddSeconds(1));
        using var service = CreateFlushService();

        await service.StartAsync(CancellationToken.None);
        await WaitUntil(() => _repository.Stats.ContainsKey("a"));
        await _buffer.RecordAsync("b", Start.AddSeconds(2));
        await WaitUntil(() => _repository.Stats.ContainsKey("b"));
        await service.StopAsync(CancellationToken.None);

        Assert.That(_repository.Stats["a"].TotalClicks, Is.EqualTo(2));
        Assert.That(_repository.Stats["b"].TotalClicks, Is.EqualTo(1));
        Assert.That(service.ExecuteTask?.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task TheFlushService_KeepsRunning_ThroughARedisOutage()
    {
        _buffer.FailPopWith = new TimeoutException("redis is down");
        await _buffer.RecordAsync("a", Start);
        using var service = CreateFlushService();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        Assert.That(service.ExecuteTask?.IsCompleted, Is.False, "an outage must not stop the service");

        _buffer.FailPopWith = null;
        await WaitUntil(() => _repository.Stats.ContainsKey("a"));
        await service.StopAsync(CancellationToken.None);

        Assert.That(_repository.Stats["a"].TotalClicks, Is.EqualTo(1));
    }

    [Test]
    public async Task TheFlushService_StopsOnAnUnexpectedError_SoTheWorkerIsRestarted()
    {
        _buffer.FailPopWith = new InvalidOperationException("a bug");
        using var service = CreateFlushService();

        await service.StartAsync(CancellationToken.None);

        await Assert.ThatAsync(() => service.ExecuteTask!, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task TheCheckpointService_SavesTheCounter_AndOnceMoreOnShutdown()
    {
        _counter.Set(42);
        using var service = CreateCheckpointService();

        await service.StartAsync(CancellationToken.None);
        await WaitUntil(() => _checkpoints.GetAsync("short_code").Result == 42);

        // Numbers handed out after the last timed save are still captured when the worker stops.
        _counter.Set(100);
        await service.StopAsync(CancellationToken.None);

        Assert.That(await _checkpoints.GetAsync("short_code"), Is.EqualTo(100));
    }

    [Test]
    public async Task TheCheckpointService_NeverLowersTheSavedCheckpoint()
    {
        await _checkpoints.SaveAsync("short_code", 5_000);
        _counter.Set(10); // Redis lost the counter and restarted low
        using var service = CreateCheckpointService();

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        Assert.That(await _checkpoints.GetAsync("short_code"), Is.EqualTo(5_000));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the background job");
            await Task.Delay(10);
        }
    }
}
