using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace UrlShortener.TestSupport;

/// <summary>One value reported to a counter, with its tags.</summary>
public sealed record CapturedMeasurement(string Instrument, long Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Collects what the application's counters report while it exists (the "UrlShortener" meter by default),
/// so tests can check that the right events were counted. Dispose it when the test ends.
/// </summary>
public sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<CapturedMeasurement> _measurements = new();

    public MetricCapture(string meterName = "UrlShortener")
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            _measurements.Enqueue(new CapturedMeasurement(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value))));
        _listener.Start();
    }

    /// <summary>The total reported to <paramref name="instrument"/>, counting only measurements that carry all the given tags.</summary>
    public long Sum(string instrument, params (string Key, string Value)[] tags) =>
        _measurements
            .Where(m => m.Instrument == instrument)
            .Where(m => tags.All(tag => m.Tags.TryGetValue(tag.Key, out var value) && Equals(value, tag.Value)))
            .Sum(m => m.Value);

    public void Dispose() => _listener.Dispose();
}

/// <summary>One log line as it was written, with its structured values and the scopes active at the time.</summary>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> State,
    IReadOnlyDictionary<string, object?> Scopes,
    string? Exception);

/// <summary>A logging provider that keeps every log line in memory, so tests can inspect what the application logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Every log line written so far.</summary>
    public IReadOnlyCollection<CapturedLog> Entries => _entries;

    /// <summary>All logged text in one string (messages, structured values, scopes and exceptions), for "never logged" checks.</summary>
    public string AllText => string.Join(
        "\n",
        _entries.Select(entry => string.Join(
            " ",
            [entry.Message, .. entry.State.Select(pair => $"{pair.Key}={pair.Value}"), .. entry.Scopes.Select(pair => $"{pair.Key}={pair.Value}"), entry.Exception])));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            var scopes = new Dictionary<string, object?>();
            owner._scopes.ForEachScope(
                (scope, target) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object>> scopePairs)
                    {
                        foreach (var pair in scopePairs)
                        {
                            target[pair.Key] = pair.Value;
                        }
                    }
                },
                scopes);

            owner._entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values, scopes, exception?.ToString()));
        }
    }
}
