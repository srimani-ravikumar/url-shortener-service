using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the click buffer against a real Redis.</summary>
public class ClickBufferTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private IClickBuffer _buffer = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ClearRedisKeysAsync();
        _buffer = Services.GetRequiredService<IClickBuffer>();
    }

    [Test]
    public async Task Take_ReturnsTheClicksRecorded_AndEmptiesTheBuffer()
    {
        await _buffer.RecordAsync("abc", Now);
        await _buffer.RecordAsync("abc", Now.AddSeconds(1));
        await _buffer.RecordAsync("abc", Now.AddSeconds(2));

        var taken = await _buffer.TakeAsync("abc");

        Assert.That(taken, Is.EqualTo(new BufferedClicks(3, Now.AddSeconds(2))));
        Assert.That(await _buffer.TakeAsync("abc"), Is.Null);
    }

    [Test]
    public async Task Take_ReturnsNull_WhenThereAreNoClicks()
    {
        Assert.That(await _buffer.TakeAsync("nobody-clicked"), Is.Null);
    }

    [Test]
    public async Task PopChangedCodes_ReturnsEachChangedCodeOnce()
    {
        await _buffer.RecordAsync("abc", Now);
        await _buffer.RecordAsync("abc", Now);

        Assert.That(await _buffer.PopChangedCodesAsync(10), Is.EqualTo(new[] { "abc" }));
        Assert.That(await _buffer.PopChangedCodesAsync(10), Is.Empty);
    }

    [Test]
    public async Task PopChangedCodes_ReturnsAtMostTheRequestedNumber()
    {
        await _buffer.RecordAsync("a", Now);
        await _buffer.RecordAsync("b", Now);
        await _buffer.RecordAsync("c", Now);

        var first = await _buffer.PopChangedCodesAsync(2);
        var second = await _buffer.PopChangedCodesAsync(2);

        Assert.That(first, Has.Count.EqualTo(2));
        Assert.That(second, Has.Count.EqualTo(1));
        Assert.That(first.Concat(second), Is.EquivalentTo(new[] { "a", "b", "c" }));
    }

    [Test]
    public async Task Restore_PutsFlushedClicksBack()
    {
        await _buffer.RecordAsync("abc", Now);
        var taken = await _buffer.TakeAsync("abc");
        await _buffer.PopChangedCodesAsync(10);

        await _buffer.RestoreAsync("abc", taken!);

        Assert.That(await _buffer.PopChangedCodesAsync(10), Is.EqualTo(new[] { "abc" }));
        Assert.That(await _buffer.TakeAsync("abc"), Is.EqualTo(taken));
    }

    [Test]
    public async Task Restore_AddsToClicksThatArrivedMeanwhile_AndKeepsTheNewerTimestamp()
    {
        // A flush took 3 clicks (last at Now), then 1 more click arrived before the flush failed.
        await _buffer.RecordAsync("abc", Now.AddMinutes(5));

        await _buffer.RestoreAsync("abc", new BufferedClicks(3, Now));

        Assert.That(await _buffer.TakeAsync("abc"), Is.EqualTo(new BufferedClicks(4, Now.AddMinutes(5))));
    }
}
