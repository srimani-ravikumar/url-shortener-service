using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Configuration;
using UrlShortener.Infrastructure;

namespace UrlShortener.AnalyticsWorker.Services;

/// <summary>Every flush interval, writes the buffered clicks from Redis to Cassandra.</summary>
internal sealed class ClickFlushService(
    ClickFlusher flusher,
    IOptions<WorkerOptions> options,
    WorkerHeartbeat heartbeat,
    ILogger<ClickFlushService> logger) : PeriodicBackgroundService(logger, heartbeat)
{
    protected override string JobName => "Click flush";

    protected override TimeSpan Interval => options.Value.FlushInterval;

    protected override async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var result = await flusher.FlushAsync(cancellationToken);

        // Quiet when there is nothing to do, so an idle system does not fill the log.
        if (result.Links > 0)
        {
            logger.LogInformation("Flushed {Clicks} clicks of {Links} links to the database.", result.Clicks, result.Links);
        }
    }
}

/// <summary>
/// Every checkpoint interval, saves the ID counter's progress to Cassandra, so that if Redis ever loses the
/// counter, the shortener can restart it past the last known value instead of reissuing old numbers.
/// </summary>
internal sealed class CounterCheckpointService(
    IdCounterDurability durability,
    IOptions<WorkerOptions> options,
    WorkerHeartbeat heartbeat,
    ILogger<CounterCheckpointService> logger) : PeriodicBackgroundService(logger, heartbeat)
{
    protected override string JobName => "Counter checkpoint";

    protected override TimeSpan Interval => options.Value.CheckpointInterval;

    protected override Task RunOnceAsync(CancellationToken cancellationToken) =>
        durability.SaveCheckpointAsync(cancellationToken);

    /// <summary>Saves one last checkpoint on a clean shutdown, so the saved value is as recent as possible.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        try
        {
            await durability.SaveCheckpointAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save the final counter checkpoint.");
        }
    }
}
