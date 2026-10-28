using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the user lookup, click statistics and counter checkpoint stores against a real Cassandra.</summary>
public class CassandraStoreTests : IntegrationTestBase
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Test]
    public async Task UserRepository_FindsTheUserOwningAnApiKeyHash()
    {
        var hash = NewApiKeyHash();
        await ExecuteCqlAsync("INSERT INTO users_by_api_key (api_key_hash, user_id) VALUES (?, ?)", hash, "user-42");
        var repository = Services.GetRequiredService<IUserRepository>();

        Assert.That(await repository.GetUserIdByApiKeyHashAsync(hash), Is.EqualTo("user-42"));
    }

    [Test]
    public async Task UserRepository_ReturnsNull_ForAnUnknownHash()
    {
        var repository = Services.GetRequiredService<IUserRepository>();

        Assert.That(await repository.GetUserIdByApiKeyHashAsync(NewApiKeyHash()), Is.Null);
    }

    [Test]
    public async Task ClickRepository_AccumulatesClicks_AndKeepsTheLatestAccessTime()
    {
        var shortCode = NewShortCode();
        var repository = Services.GetRequiredService<IClickRepository>();

        await repository.AddClicksAsync(shortCode, 5, Now);
        await repository.AddClicksAsync(shortCode, 3, Now.AddMinutes(1));
        var stats = await repository.GetAsync(shortCode);

        Assert.That(stats.TotalClicks, Is.EqualTo(8));
        Assert.That(stats.LastAccessed, Is.EqualTo(Now.AddMinutes(1)));
    }

    [Test]
    public async Task ClickRepository_ReportsZeroClicks_ForALinkNobodyClicked()
    {
        var repository = Services.GetRequiredService<IClickRepository>();

        var stats = await repository.GetAsync(NewShortCode());

        Assert.That(stats, Is.EqualTo(new ClickStats(0, null)));
    }

    [Test]
    public async Task CheckpointStore_ReturnsNull_UntilSomethingIsSaved()
    {
        var store = Services.GetRequiredService<IIdCounterCheckpointStore>();

        // A name nothing else writes to, so test order cannot matter.
        Assert.That(await store.GetAsync(CheckpointName + "-never-saved"), Is.Null);
    }

    [Test]
    public async Task CheckpointStore_SavesAndOverwritesTheValue()
    {
        var store = Services.GetRequiredService<IIdCounterCheckpointStore>();

        await store.SaveAsync(CheckpointName, 1_000);
        Assert.That(await store.GetAsync(CheckpointName), Is.EqualTo(1_000));

        await store.SaveAsync(CheckpointName, 2_500);
        Assert.That(await store.GetAsync(CheckpointName), Is.EqualTo(2_500));
    }
}
