using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UrlShortener.Infrastructure.Telemetry;

/// <summary>
/// The traces and metrics the application produces itself (the framework adds its own: HTTP requests, runtime).
/// Everything is a no-op until OpenTelemetry (or a test) listens, so calling these on the hot path is cheap.
/// </summary>
public static class UrlShortenerTelemetry
{
    /// <summary>Name of the activity source and meter; exporters subscribe to it by this name.</summary>
    public const string Name = "UrlShortener";

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> Redirects = Meter.CreateCounter<long>(
        "urlshortener.redirects",
        description: "Short link requests by result: found, not_found, expired or invalid_code.");

    private static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>(
        "urlshortener.cache.lookups",
        description: "Redirect cache lookups by result: hit or miss (a miss also covers an unreachable cache).");

    private static readonly Counter<long> ShortenRequests = Meter.CreateCounter<long>(
        "urlshortener.shorten.requests",
        description: "Link creation requests by outcome: created, invalid, alias_taken or generation_failed.");

    private static readonly Counter<long> ClicksFlushed = Meter.CreateCounter<long>(
        "urlshortener.clicks.flushed",
        description: "Clicks written from the Redis buffer to the database.");

    private static readonly Counter<long> ClickFlushFailures = Meter.CreateCounter<long>(
        "urlshortener.clicks.flush.failures",
        description: "Links whose clicks could not be written and were put back in the buffer.");

    /// <summary>Source of the application's own trace spans (database and cache calls).</summary>
    public static ActivitySource ActivitySource { get; } = new(Name);

    /// <summary>Counts a short link request. <paramref name="result"/> is found, not_found, expired or invalid_code.</summary>
    public static void RecordRedirect(string result) => Redirects.Add(1, new TagList { { "result", result } });

    /// <summary>Counts a redirect cache lookup.</summary>
    public static void RecordCacheLookup(bool hit) => CacheLookups.Add(1, new TagList { { "result", hit ? "hit" : "miss" } });

    /// <summary>Counts a link creation request. <paramref name="outcome"/> is created, invalid, alias_taken or generation_failed.</summary>
    public static void RecordShorten(string outcome) => ShortenRequests.Add(1, new TagList { { "outcome", outcome } });

    /// <summary>Counts clicks that were written to the database.</summary>
    public static void RecordClicksFlushed(long clicks) => ClicksFlushed.Add(clicks);

    /// <summary>Counts links whose click write failed.</summary>
    public static void RecordClickFlushFailures(long links) => ClickFlushFailures.Add(links);

    /// <summary>
    /// Starts a trace span for a call to a database or cache (null when nobody is listening).
    /// Dispose it when the call ends.
    /// </summary>
    /// <param name="spanName">A short, fixed name such as "cassandra.execute" (never include ids or values).</param>
    /// <param name="system">The kind of system called: "cassandra" or "redis".</param>
    public static Activity? StartClientSpan(string spanName, string system)
    {
        var span = ActivitySource.StartActivity(spanName, ActivityKind.Client);
        span?.SetTag("db.system", system);
        return span;
    }
}
