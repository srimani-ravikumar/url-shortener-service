using System.Diagnostics;
using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Configuration;
using UrlShortener.Core.Storage;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Telemetry;

namespace UrlShortener.AnalyticsWorker.Services;

/// <summary>What one flush did.</summary>
/// <param name="Links">How many links had their clicks written.</param>
/// <param name="Clicks">How many clicks were written in total.</param>
/// <param name="Failures">How many links could not be written (their clicks were put back for the next flush).</param>
internal sealed record FlushResult(int Links, long Clicks, int Failures);

/// <summary>
/// Moves clicks from the Redis buffer into Cassandra.
/// The redirect service only counts clicks in Redis; this is what makes them durable and visible in the analytics API.
/// </summary>
/// <remarks>
/// Delivery is "at least once, almost exactly once". A click is removed from Redis before it is written, and put
/// back if the write fails, so an ordinary Cassandra outage loses nothing. Two rare cases remain, because a
/// Cassandra counter update cannot be repeated safely: if this process dies between removing and writing, those
/// clicks are lost; if a write succeeds but reports a timeout, the retry counts those clicks twice.
/// </remarks>
internal sealed class ClickFlusher(
    IClickBuffer buffer,
    IClickRepository repository,
    IOptions<WorkerOptions> options,
    ILogger<ClickFlusher> logger)
{
    private readonly WorkerOptions _options = options.Value;

    // Cassandra can fail the same way for every link; log that once in a while, not once per link.
    private readonly ThrottledLogger _writeFailures = new(logger, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Flushes the links that have buffered clicks, for at most one flush interval (clicks keep arriving, so
    /// "until the buffer is empty" might never happen). Cancellation is only honoured between batches: a batch
    /// that has been taken from Redis is always finished (or put back), so stopping the worker loses nothing.
    /// </summary>
    public async Task<FlushResult> FlushAsync(CancellationToken cancellationToken)
    {
        var links = 0;
        long clicks = 0;
        var failures = 0;
        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await buffer.PopChangedCodesAsync(_options.FlushBatchSize, CancellationToken.None);
            if (batch.Count == 0)
            {
                break;
            }

            var failedInBatch = 0;
            await Parallel.ForEachAsync(
                batch,
                new ParallelOptions { MaxDegreeOfParallelism = _options.FlushConcurrency },
                async (shortCode, _) =>
                {
                    var written = await FlushLinkAsync(shortCode);
                    if (written is null)
                    {
                        Interlocked.Increment(ref failedInBatch);
                    }
                    else if (written > 0)
                    {
                        Interlocked.Increment(ref links);
                        Interlocked.Add(ref clicks, written.Value);
                    }
                });
            failures += failedInBatch;

            // If not a single link of a batch could be written, the database is most likely down:
            // wait for the next cycle instead of taking and putting back the same links in a loop.
            if (failedInBatch == batch.Count)
            {
                break;
            }

            // Popular links get new clicks while they are being flushed; leave those for the next cycle.
            if (Stopwatch.GetElapsedTime(started) >= _options.FlushInterval)
            {
                break;
            }
        }

        UrlShortenerTelemetry.RecordClicksFlushed(clicks);
        UrlShortenerTelemetry.RecordClickFlushFailures(failures);
        return new FlushResult(links, clicks, failures);
    }

    // Returns how many clicks were written (0 if there was nothing to write), or null if the write failed.
    private async Task<long?> FlushLinkAsync(string shortCode)
    {
        var taken = await buffer.TakeAsync(shortCode, CancellationToken.None);
        if (taken is null)
        {
            return 0;
        }

        try
        {
            await repository.AddClicksAsync(shortCode, taken.Count, taken.LastAccessed, CancellationToken.None);
            return taken.Count;
        }
        catch (Exception ex)
        {
            _writeFailures.Warn(ex, "Could not write clicks to the database; they stay buffered and will be retried.");
            await PutBackAsync(shortCode, taken);
            return null;
        }
    }

    private async Task PutBackAsync(string shortCode, BufferedClicks clicks)
    {
        try
        {
            await buffer.RestoreAsync(shortCode, clicks, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Both Cassandra and Redis are failing at once; nothing more can be done for these clicks.
            logger.LogError(ex, "Could not put back {Count} clicks of {ShortCode}; they are lost.", clicks.Count, shortCode);
        }
    }
}
