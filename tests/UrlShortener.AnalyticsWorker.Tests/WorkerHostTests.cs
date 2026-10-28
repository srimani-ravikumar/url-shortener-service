using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Extensions;
using UrlShortener.Core.Storage;
using UrlShortener.TestSupport;

namespace UrlShortener.AnalyticsWorker.Tests;

/// <summary>Starts the worker through its real service registration, with fake storage.</summary>
public class WorkerHostTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeClickBuffer _buffer = new();
    private readonly FakeClickRepository _repository = new();
    private readonly FakeIdCounter _counter = new();
    private readonly FakeCheckpointStore _checkpoints = new();

    private IHost BuildHost(params (string Key, string Value)[] settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
        builder.Services.AddLogging();
        builder.Services.AddAnalyticsWorker(builder.Configuration);

        Replace<IClickBuffer>(builder.Services, _buffer);
        Replace<IClickRepository>(builder.Services, _repository);
        Replace<IIdCounter>(builder.Services, _counter);
        Replace<IIdCounterCheckpointStore>(builder.Services, _checkpoints);
        return builder.Build();
    }

    [Test]
    public async Task TheWorker_FlushesClicks_AndSavesCheckpoints()
    {
        await _buffer.RecordAsync("a", Start);
        _counter.Set(7);
        using var host = BuildHost(("Worker:FlushInterval", "00:00:00.050"), ("Worker:CheckpointInterval", "00:00:00.050"));

        await host.StartAsync();
        await WaitUntil(() => _repository.Stats.ContainsKey("a") && _checkpoints.GetAsync("short_code").Result == 7);
        await host.StopAsync();

        Assert.That(_repository.Stats["a"].TotalClicks, Is.EqualTo(1));
    }

    [TestCase("Worker:FlushInterval", "00:00:00")]
    [TestCase("Worker:FlushBatchSize", "0")]
    [TestCase("Worker:FlushConcurrency", "1000")]
    [TestCase("Worker:CheckpointInterval", "-00:00:05")]
    public async Task TheWorker_RefusesToStart_WithInvalidSettings(string key, string value)
    {
        using var host = BuildHost((key, value));

        await Assert.ThatAsync(() => host.StartAsync(), Throws.TypeOf<OptionsValidationException>());
    }

    private static void Replace<T>(IServiceCollection services, T instance)
        where T : class
    {
        services.RemoveAll<T>();
        services.AddSingleton(instance);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "timed out waiting for the worker");
            await Task.Delay(10);
        }
    }
}
