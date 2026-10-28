using UrlShortener.Infrastructure;

namespace UrlShortener.AnalyticsWorker.Services;

/// <summary>
/// A background job that runs once at startup and then every <see cref="Interval"/>. A storage outage
/// (Redis or Cassandra unavailable) is logged and retried on the next tick; any other exception is a bug
/// and stops the worker, so the container is restarted instead of silently doing nothing.
/// </summary>
internal abstract class PeriodicBackgroundService(ILogger logger, WorkerHeartbeat heartbeat) : BackgroundService
{
    private readonly ThrottledLogger _outages = new(logger, TimeSpan.FromSeconds(30));

    /// <summary>What the job does, for log messages.</summary>
    protected abstract string JobName { get; }

    protected abstract TimeSpan Interval { get; }

    /// <summary>One run of the job.</summary>
    protected abstract Task RunOnceAsync(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (TransientFailure.IsTransient(ex))
                {
                    _outages.Warn(ex, $"{JobName} could not run because storage is unavailable; will retry.");
                }

                // The loop is turning (even if storage was down), which is what the liveness probe checks.
                heartbeat.Beat(JobName, Interval);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The worker is shutting down.
        }
    }
}
