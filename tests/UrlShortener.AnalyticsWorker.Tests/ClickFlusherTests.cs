using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UrlShortener.AnalyticsWorker.Configuration;
using UrlShortener.AnalyticsWorker.Services;
using UrlShortener.Core.Storage;
using UrlShortener.TestSupport;

namespace UrlShortener.AnalyticsWorker.Tests;

/// <summary>Tests that buffered clicks reach the database, and that no click is lost when something goes wrong.</summary>
public class ClickFlusherTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private FakeClickBuffer _buffer = null!;
    private FakeClickRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _buffer = new FakeClickBuffer();
        _repository = new FakeClickRepository();
    }

    private ClickFlusher CreateFlusher(int batchSize = 500, TimeSpan? flushInterval = null) => new(
        _buffer,
        _repository,
        Options.Create(new WorkerOptions { FlushBatchSize = batchSize, FlushConcurrency = 4, FlushInterval = flushInterval ?? TimeSpan.FromSeconds(10) }),
        NullLogger<ClickFlusher>.Instance);

    private async Task Click(string code, int times, int secondOffset = 0)
    {
        for (var i = 0; i < times; i++)
        {
            await _buffer.RecordAsync(code, Start.AddSeconds(secondOffset + i));
        }
    }

    [Test]
    public async Task Flush_WritesTheBufferedClicks_AndEmptiesTheBuffer()
    {
        await Click("a", 3);
        await Click("b", 2, secondOffset: 10);

        var result = await CreateFlusher().FlushAsync(CancellationToken.None);

        Assert.That(result, Is.EqualTo(new FlushResult(Links: 2, Clicks: 5, Failures: 0)));
        Assert.That(_repository.Stats["a"], Is.EqualTo(new ClickStats(3, Start.AddSeconds(2))));
        Assert.That(_repository.Stats["b"], Is.EqualTo(new ClickStats(2, Start.AddSeconds(11))));
        Assert.That(_buffer.PendingLinkCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Flush_AddsToWhatWasWrittenBefore()
    {
        var flusher = CreateFlusher();
        await Click("a", 3);
        await flusher.FlushAsync(CancellationToken.None);

        await Click("a", 2, secondOffset: 100);
        await flusher.FlushAsync(CancellationToken.None);

        Assert.That(_repository.Stats["a"], Is.EqualTo(new ClickStats(5, Start.AddSeconds(101))));
    }

    [Test]
    public async Task Flush_DoesNothing_WhenThereAreNoClicks()
    {
        var result = await CreateFlusher().FlushAsync(CancellationToken.None);

        Assert.That(result, Is.EqualTo(new FlushResult(0, 0, 0)));
        Assert.That(_repository.AddCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Flush_HandlesMoreLinksThanFitInOneBatch()
    {
        for (var i = 0; i < 25; i++)
        {
            await Click($"link-{i}", 2);
        }

        var result = await CreateFlusher(batchSize: 10).FlushAsync(CancellationToken.None);

        Assert.That(result, Is.EqualTo(new FlushResult(Links: 25, Clicks: 50, Failures: 0)));
        Assert.That(_repository.Stats, Has.Count.EqualTo(25));
    }

    [Test]
    public async Task Flush_PutsClicksBack_WhenTheDatabaseFails_AndWritesThemExactlyOnceLater()
    {
        var flusher = CreateFlusher();
        await Click("a", 3);
        _repository.FailWith = new TimeoutException("cassandra timed out");

        var failed = await flusher.FlushAsync(CancellationToken.None);

        Assert.That(failed, Is.EqualTo(new FlushResult(Links: 0, Clicks: 0, Failures: 1)));
        Assert.That(_repository.Stats, Is.Empty);
        Assert.That(_buffer.Pending("a"), Is.EqualTo(new BufferedClicks(3, Start.AddSeconds(2))));

        _repository.FailWith = null;
        var recovered = await flusher.FlushAsync(CancellationToken.None);

        Assert.That(recovered, Is.EqualTo(new FlushResult(Links: 1, Clicks: 3, Failures: 0)));
        Assert.That(_repository.Stats["a"].TotalClicks, Is.EqualTo(3));
    }

    [Test]
    public async Task Flush_StopsAfterABatchThatFailedCompletely_InsteadOfLooping()
    {
        for (var i = 0; i < 10; i++)
        {
            await Click($"link-{i}", 1);
        }

        _repository.FailWith = new TimeoutException();

        var result = await CreateFlusher(batchSize: 2).FlushAsync(CancellationToken.None);

        // Only the first batch of 2 was tried, and all 10 links are still waiting to be written.
        Assert.That(result.Failures, Is.EqualTo(2));
        Assert.That(_repository.AddCalls, Is.EqualTo(2));
        Assert.That(_buffer.PendingLinkCount, Is.EqualTo(10));
    }

    [Test]
    public async Task Flush_KeepsGoing_WhenOnlySomeLinksFail()
    {
        await Click("ok-1", 1);
        await Click("bad", 4);
        await Click("ok-2", 1);
        _repository.OnAddClicks = code =>
        {
            if (code == "bad")
            {
                throw new TimeoutException("this one link failed");
            }
        };

        var result = await CreateFlusher().FlushAsync(CancellationToken.None);

        // The failed link is put back, so it can be picked up (and fail) again within the same flush.
        Assert.That(result.Links, Is.EqualTo(2));
        Assert.That(result.Clicks, Is.EqualTo(2));
        Assert.That(result.Failures, Is.GreaterThanOrEqualTo(1));
        Assert.That(_buffer.Pending("bad")?.Count, Is.EqualTo(4));
    }

    [Test]
    public async Task Flush_DoesNotLoseClicksThatArriveWhileFlushing()
    {
        var flusher = CreateFlusher();
        await Click("a", 3);

        // A visitor follows the link just as its clicks are being written (only the first time it is written).
        var clickedMidFlush = false;
        _repository.OnAddClicks = code =>
        {
            if (!clickedMidFlush)
            {
                clickedMidFlush = true;
                _buffer.RecordAsync(code, Start.AddMinutes(1)).GetAwaiter().GetResult();
            }
        };
        await flusher.FlushAsync(CancellationToken.None);
        _repository.OnAddClicks = null;

        // The in-flight click was not part of the write, so it must still be waiting, not lost.
        // (The flush loop may already have written it in a second pass, which is also correct.)
        var written = _repository.Stats["a"].TotalClicks;
        var waiting = _buffer.Pending("a")?.Count ?? 0;
        Assert.That(written + waiting, Is.EqualTo(4));

        await flusher.FlushAsync(CancellationToken.None);
        Assert.That(_repository.Stats["a"].TotalClicks, Is.EqualTo(4));
    }

    [Test]
    public async Task Flush_ReturnsAfterOneFlushInterval_WhenClicksKeepArriving()
    {
        await Click("busy", 1);

        // Every write is immediately followed by another click, so the buffer is never empty.
        _repository.OnAddClicks = code => _buffer.RecordAsync(code, Start).GetAwaiter().GetResult();
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateFlusher(flushInterval: TimeSpan.FromMilliseconds(100)).FlushAsync(CancellationToken.None);

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
        Assert.That(result.Links, Is.GreaterThan(0));
    }

    [Test]
    public async Task Flush_FinishesTheBatchItTook_EvenWhenStoppingMidFlush()
    {
        using var cancellation = new CancellationTokenSource();
        for (var i = 0; i < 5; i++)
        {
            await Click($"link-{i}", 1);
        }

        _repository.OnAddClicks = _ => cancellation.Cancel();

        // The stop request is noticed before the next batch, not in the middle of this one.
        await Assert.ThatAsync(() => CreateFlusher().FlushAsync(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());

        Assert.That(_repository.Stats, Has.Count.EqualTo(5));
        Assert.That(_buffer.PendingLinkCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Flush_DoesNotThrow_WhenPuttingClicksBackAlsoFails()
    {
        await Click("a", 3);
        _repository.FailWith = new TimeoutException();
        _buffer.FailRestoreWith = new TimeoutException();

        var result = await CreateFlusher().FlushAsync(CancellationToken.None);

        Assert.That(result.Failures, Is.EqualTo(1));
    }

    [Test]
    public async Task Flush_CountsTheClicksItWritesAndTheLinksItCouldNotWrite()
    {
        using var metrics = new MetricCapture();
        var flusher = CreateFlusher();
        await Click("a", 4);
        await flusher.FlushAsync(CancellationToken.None);

        await Click("b", 2);
        _repository.FailWith = new TimeoutException();
        await flusher.FlushAsync(CancellationToken.None);

        Assert.That(metrics.Sum("urlshortener.clicks.flushed"), Is.EqualTo(4));
        Assert.That(metrics.Sum("urlshortener.clicks.flush.failures"), Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Flush_PassesOnARedisOutage_ForTheCallerToRetry()
    {
        _buffer.FailPopWith = new TimeoutException("redis timed out");

        Assert.That(() => CreateFlusher().FlushAsync(CancellationToken.None), Throws.TypeOf<TimeoutException>());
    }
}
