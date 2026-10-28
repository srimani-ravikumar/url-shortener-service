using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the ID counter and its durability logic against a real Redis and Cassandra.</summary>
public class IdCounterTests : IntegrationTestBase
{
    private IIdCounter _counter = null!;
    private IIdCounterCheckpointStore _checkpoints = null!;
    private IdCounterDurability _durability = null!;

    [SetUp]
    public async Task SetUp()
    {
        // Each test starts from "Redis has no counter and nothing was ever checkpointed".
        await ClearRedisKeysAsync();
        await ExecuteCqlAsync("DELETE FROM id_counter_checkpoint WHERE name = ?", CheckpointName);

        _counter = Services.GetRequiredService<IIdCounter>();
        _checkpoints = Services.GetRequiredService<IIdCounterCheckpointStore>();
        _durability = Services.GetRequiredService<IdCounterDurability>();
    }

    [Test]
    public async Task Next_StartsAtOne_AndCountsUp()
    {
        Assert.That(await _counter.NextAsync(), Is.EqualTo(1));
        Assert.That(await _counter.NextAsync(), Is.EqualTo(2));
        Assert.That(await _counter.NextAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task Next_NeverRepeatsANumber_UnderConcurrency()
    {
        var numbers = await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => _counter.NextAsync()));

        Assert.That(numbers.Distinct().Count(), Is.EqualTo(500));
    }

    [Test]
    public async Task GetCurrent_IsNull_UntilTheFirstNumberIsIssued_ThenTracksTheLastOne()
    {
        Assert.That(await _counter.GetCurrentAsync(), Is.Null);

        await _counter.NextAsync();
        await _counter.NextAsync();

        Assert.That(await _counter.GetCurrentAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task ReseedIfBehind_MovesAMissingCounterPastTheCheckpoint()
    {
        var reseeded = await _counter.ReseedIfBehindAsync(checkpoint: 1_000, safetyGap: 50);

        Assert.That(reseeded, Is.True);
        Assert.That(await _counter.NextAsync(), Is.EqualTo(1_051));
    }

    [Test]
    public async Task ReseedIfBehind_MovesACounterThatFellBehindTheCheckpoint()
    {
        await Redis.StringSetAsync(KeyPrefix + "id:counter", 10);

        var reseeded = await _counter.ReseedIfBehindAsync(checkpoint: 1_000, safetyGap: 50);

        Assert.That(reseeded, Is.True);
        Assert.That(await _counter.GetCurrentAsync(), Is.EqualTo(1_050));
    }

    [Test]
    public async Task ReseedIfBehind_LeavesAHealthyCounterAlone()
    {
        await Redis.StringSetAsync(KeyPrefix + "id:counter", 5_000);

        var reseeded = await _counter.ReseedIfBehindAsync(checkpoint: 1_000, safetyGap: 50);

        Assert.That(reseeded, Is.False);
        Assert.That(await _counter.GetCurrentAsync(), Is.EqualTo(5_000));
    }

    [Test]
    public async Task RestoreIfLost_DoesNothing_WhenNoCheckpointWasEverSaved()
    {
        Assert.That(await _durability.RestoreIfLostAsync(), Is.False);
        Assert.That(await _counter.GetCurrentAsync(), Is.Null);
    }

    [Test]
    public async Task RestoreIfLost_MovesTheCounterPastTheCheckpoint_WhenRedisLostIt()
    {
        await _checkpoints.SaveAsync(CheckpointName, 5_000);

        var restored = await _durability.RestoreIfLostAsync();

        // 5,000 + the default safety gap of 100,000.
        Assert.That(restored, Is.True);
        Assert.That(await _counter.NextAsync(), Is.EqualTo(105_001));
    }

    [Test]
    public async Task RestoreIfLost_LeavesTheCounterAlone_WhenItIsAheadOfTheCheckpoint()
    {
        await _checkpoints.SaveAsync(CheckpointName, 5_000);
        await Redis.StringSetAsync(KeyPrefix + "id:counter", 6_000);

        Assert.That(await _durability.RestoreIfLostAsync(), Is.False);
        Assert.That(await _counter.GetCurrentAsync(), Is.EqualTo(6_000));
    }

    [Test]
    public async Task SaveCheckpoint_StoresTheCurrentCounterValue()
    {
        await _counter.NextAsync();
        await _counter.NextAsync();
        await _counter.NextAsync();

        await _durability.SaveCheckpointAsync();

        Assert.That(await _checkpoints.GetAsync(CheckpointName), Is.EqualTo(3));
    }

    [Test]
    public async Task SaveCheckpoint_NeverMovesTheCheckpointBackwards()
    {
        // Redis lost its counter and restarted at a low value before recovery ran.
        await _checkpoints.SaveAsync(CheckpointName, 5_000);
        await Redis.StringSetAsync(KeyPrefix + "id:counter", 10);

        await _durability.SaveCheckpointAsync();

        Assert.That(await _checkpoints.GetAsync(CheckpointName), Is.EqualTo(5_000));
    }

    [Test]
    public async Task SaveCheckpoint_DoesNothing_WhenThereIsNoCounter()
    {
        await _durability.SaveCheckpointAsync();

        Assert.That(await _checkpoints.GetAsync(CheckpointName), Is.Null);
    }
}
