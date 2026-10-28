using System.ComponentModel.DataAnnotations;

namespace UrlShortener.AnalyticsWorker.Configuration;

/// <summary>Settings of the analytics worker (configuration section "Worker").</summary>
internal sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>How often buffered clicks are written to Cassandra. Click counts in the API lag by up to this long.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many links are taken from Redis and written at a time.</summary>
    [Range(1, 10_000)]
    public int FlushBatchSize { get; set; } = 500;

    /// <summary>How many links of a batch are written to Cassandra at the same time.</summary>
    [Range(1, 64)]
    public int FlushConcurrency { get; set; } = 8;

    /// <summary>
    /// How often the ID counter's progress is saved to Cassandra. The "IdCounter:SafetyGap" setting must be
    /// larger than the ids that can be handed out in this time (interval x peak creates per second).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "01:00:00")]
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(30);
}
