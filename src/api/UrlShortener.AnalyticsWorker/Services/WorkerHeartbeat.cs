using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Configuration;

namespace UrlShortener.AnalyticsWorker.Services;

/// <summary>
/// Remembers when each background job last completed a run (successful or not). A job that stops beating is
/// stuck, which the liveness probe reports so the container can be restarted.
/// </summary>
internal sealed class WorkerHeartbeat(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (long Ticks, TimeSpan Interval)> _beats = new();

    /// <summary>Records that <paramref name="job"/> just finished a run and expects to run again within <paramref name="interval"/>.</summary>
    public void Beat(string job, TimeSpan interval) => _beats[job] = (time.GetUtcNow().UtcTicks, interval);

    /// <summary>The jobs that have not finished a run for much longer than their interval.</summary>
    public IReadOnlyList<string> StalledJobs()
    {
        var now = time.GetUtcNow().UtcTicks;
        return _beats
            .Where(beat => TimeSpan.FromTicks(now - beat.Value.Ticks) > AllowedSilence(beat.Value.Interval))
            .Select(beat => beat.Key)
            .ToList();
    }

    // A run can legitimately take up to one interval (a long flush), so allow three before calling it stuck.
    private static TimeSpan AllowedSilence(TimeSpan interval) => TimeSpan.FromTicks(Math.Max(interval.Ticks * 3, TimeSpan.FromSeconds(30).Ticks));
}

/// <summary>Unhealthy if a background job has stopped running (the process is up but doing nothing).</summary>
internal sealed class WorkerHeartbeatHealthCheck(WorkerHeartbeat heartbeat) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stalled = heartbeat.StalledJobs();
        return Task.FromResult(stalled.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Not running: {string.Join(", ", stalled)}."));
    }
}
