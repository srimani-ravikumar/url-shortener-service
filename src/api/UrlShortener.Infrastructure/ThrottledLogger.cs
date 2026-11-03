using Microsoft.Extensions.Logging;

namespace UrlShortener.Infrastructure;

/// <summary>
/// Logs a recurring failure at most once per interval. During an outage a hot path (every redirect) fails
/// the same way thousands of times a second; one line per interval, with a count of the rest, keeps the logs
/// readable and cheap. Thread-safe.
/// </summary>
public sealed class ThrottledLogger(ILogger logger, TimeSpan interval, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastLoggedTicks = long.MinValue;
    private int _suppressed;

    /// <summary>Logs the warning unless one was logged within the interval; the exception is attached once per interval.</summary>
    public void Warn(Exception exception, string message)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastLoggedTicks);

        // Only the caller that wins the race to move the timestamp logs; everyone else just counts.
        var due = last == long.MinValue || now - last >= interval.Ticks;
        if (!due || Interlocked.CompareExchange(ref _lastLoggedTicks, now, last) != last)
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressed, 0);
        logger.LogWarning(
            exception,
            "{Message} ({Suppressed} similar failures since the last message, which are not logged individually.)",
            message,
            suppressed);
    }
}
