using Microsoft.Extensions.Logging;
using UrlShortener.TestSupport;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Tests that a failure repeated thousands of times is logged once per interval.</summary>
public class ThrottledLoggerTests
{
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private readonly ListLogger _logger = new();
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    private ThrottledLogger _throttled = null!;

    [SetUp]
    public void SetUp()
    {
        _logger.Entries.Clear();
        _throttled = new ThrottledLogger(_logger, TimeSpan.FromSeconds(30), _time);
    }

    [Test]
    public void TheFirstFailureIsLoggedImmediately_WithTheException()
    {
        var failure = new InvalidOperationException("redis down");

        _throttled.Warn(failure, "Redis read failed.");

        Assert.That(_logger.Entries, Has.Count.EqualTo(1));
        Assert.That(_logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(_logger.Entries[0].Text, Does.StartWith("Redis read failed."));
        Assert.That(_logger.Entries[0].Exception, Is.SameAs(failure));
    }

    [Test]
    public void RepeatedFailuresWithinTheIntervalAreNotLogged()
    {
        for (var i = 0; i < 1_000; i++)
        {
            _throttled.Warn(new InvalidOperationException("redis down"), "Redis read failed.");
            _time.Now += TimeSpan.FromMilliseconds(10);
        }

        // 10 seconds passed in total, so only the first one was logged.
        Assert.That(_logger.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void AfterTheInterval_ItLogsAgain_AndSaysHowManyWereSuppressed()
    {
        _throttled.Warn(new InvalidOperationException("redis down"), "Redis read failed.");
        for (var i = 0; i < 5; i++)
        {
            _throttled.Warn(new InvalidOperationException("redis down"), "Redis read failed.");
        }

        _time.Now += TimeSpan.FromSeconds(31);
        _throttled.Warn(new InvalidOperationException("redis down"), "Redis read failed.");

        Assert.That(_logger.Entries, Has.Count.EqualTo(2));
        Assert.That(_logger.Entries[1].Text, Does.Contain("5 similar failures"));
    }
}
