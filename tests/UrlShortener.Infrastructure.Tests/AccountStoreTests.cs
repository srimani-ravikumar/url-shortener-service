using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the account, email token and cooldown stores against a real Cassandra and Redis.</summary>
public class AccountStoreTests : IntegrationTestBase
{
    // Cassandra stores timestamps with millisecond precision, so test values are truncated to match.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private IUserRepository _users = null!;

    [SetUp]
    public void SetUp() => _users = Services.GetRequiredService<IUserRepository>();

    private UserAccount NewAccount(string? email = null) => new(NewUserId(), email ?? NewEmail(), Now, NewApiKeyHash());

    [Test]
    public async Task TryCreate_StoresTheAccount_ReachableByIdEmailAndKey()
    {
        var account = NewAccount();

        var created = await _users.TryCreateAsync(account);

        Assert.That(created, Is.True);
        Assert.That(await _users.GetByIdAsync(account.UserId), Is.EqualTo(account));
        Assert.That(await _users.GetByEmailAsync(account.Email), Is.EqualTo(account));
        Assert.That(await _users.GetUserIdByApiKeyHashAsync(account.ApiKeyHash!), Is.EqualTo(account.UserId));
    }

    [Test]
    public async Task TryCreate_ReturnsFalse_ForATakenEmail_AndCreatesNothing()
    {
        var first = NewAccount();
        await _users.TryCreateAsync(first);
        var second = NewAccount(first.Email);

        var created = await _users.TryCreateAsync(second);

        Assert.That(created, Is.False);
        Assert.That(await _users.GetByIdAsync(second.UserId), Is.Null);
        Assert.That(await _users.GetUserIdByApiKeyHashAsync(second.ApiKeyHash!), Is.Null);
        Assert.That(await _users.GetByEmailAsync(first.Email), Is.EqualTo(first));
    }

    [Test]
    public async Task TryCreate_OnlyOneOfManyConcurrentClaimsOfAnEmailWins()
    {
        var email = NewEmail();
        var attempts = Enumerable.Range(0, 20).Select(_ => _users.TryCreateAsync(NewAccount(email)));

        var results = await Task.WhenAll(attempts);

        Assert.That(results.Count(created => created), Is.EqualTo(1));
    }

    [Test]
    public async Task GetByIdAndEmail_ReturnNull_ForUnknownValues()
    {
        Assert.That(await _users.GetByIdAsync("it-user-unknown"), Is.Null);
        Assert.That(await _users.GetByEmailAsync("it-unknown@example.test"), Is.Null);
    }

    [Test]
    public async Task ReplaceApiKey_RevokesTheOldKey_AndTheNewOneWorks()
    {
        var account = NewAccount();
        await _users.TryCreateAsync(account);
        var newHash = NewApiKeyHash();

        await _users.ReplaceApiKeyAsync(account.UserId, newHash, presentedApiKeyHash: null);

        Assert.That(await _users.GetUserIdByApiKeyHashAsync(account.ApiKeyHash!), Is.Null);
        Assert.That(await _users.GetUserIdByApiKeyHashAsync(newHash), Is.EqualTo(account.UserId));
        Assert.That((await _users.GetByIdAsync(account.UserId))!.ApiKeyHash, Is.EqualTo(newHash));
    }

    [Test]
    public async Task ReplaceApiKey_AlsoRevokesThePresentedKey_WhenItIsNotTheRecordedOne()
    {
        // An account created before the key hash was recorded on the user row.
        var account = NewAccount();
        await _users.TryCreateAsync(account);
        await ExecuteCqlAsync("UPDATE users SET api_key_hash = null WHERE user_id = ?", account.UserId);
        var newHash = NewApiKeyHash();

        await _users.ReplaceApiKeyAsync(account.UserId, newHash, presentedApiKeyHash: account.ApiKeyHash);

        Assert.That(await _users.GetUserIdByApiKeyHashAsync(account.ApiKeyHash!), Is.Null);
        Assert.That(await _users.GetUserIdByApiKeyHashAsync(newHash), Is.EqualTo(account.UserId));
    }

    [Test]
    public async Task Delete_RemovesTheAccountItsKeyAndItsEmailClaim()
    {
        var account = NewAccount();
        await _users.TryCreateAsync(account);

        await _users.DeleteAsync(account, presentedApiKeyHash: null);

        Assert.That(await _users.GetByIdAsync(account.UserId), Is.Null);
        Assert.That(await _users.GetByEmailAsync(account.Email), Is.Null);
        Assert.That(await _users.GetUserIdByApiKeyHashAsync(account.ApiKeyHash!), Is.Null);

        // The address can be claimed again.
        Assert.That(await _users.TryCreateAsync(NewAccount(account.Email)), Is.True);
    }

    [Test]
    public async Task EmailTokens_AreSavedAndCanBeTakenOnce()
    {
        var store = Services.GetRequiredService<IEmailTokenStore>();
        var hash = NewTokenHash();
        var token = new EmailToken(EmailTokenPurposes.SignUp, NewEmail(), Now);
        await store.SaveAsync(hash, token, TimeSpan.FromHours(1));

        var first = await store.TakeAsync(hash);
        var second = await store.TakeAsync(hash);

        Assert.That(first, Is.EqualTo(token));
        Assert.That(second, Is.Null);
    }

    [Test]
    public async Task EmailTokens_OnlyOneOfManyConcurrentTakesGetsTheToken()
    {
        var store = Services.GetRequiredService<IEmailTokenStore>();
        var hash = NewTokenHash();
        await store.SaveAsync(hash, new EmailToken(EmailTokenPurposes.Recover, NewEmail(), Now), TimeSpan.FromHours(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.TakeAsync(hash)));

        Assert.That(results.Count(result => result is not null), Is.EqualTo(1));
    }

    [Test]
    public async Task EmailTokens_AreUnknown_ForAnUnissuedHash()
    {
        var store = Services.GetRequiredService<IEmailTokenStore>();

        Assert.That(await store.TakeAsync(NewTokenHash()), Is.Null);
    }

    [Test]
    public async Task EmailTokens_ExpireByThemselves()
    {
        var store = Services.GetRequiredService<IEmailTokenStore>();
        var hash = NewTokenHash();
        await store.SaveAsync(hash, new EmailToken(EmailTokenPurposes.SignUp, NewEmail(), Now), TimeSpan.FromSeconds(1));

        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        Assert.That(await store.TakeAsync(hash), Is.Null);
    }

    [Test]
    public async Task Cooldown_AllowsOneStartUntilItRunsOut()
    {
        var cooldowns = Services.GetRequiredService<ICooldownStore>();
        var key = "it-" + Guid.NewGuid().ToString("N");

        Assert.That(await cooldowns.TryStartAsync(key, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(await cooldowns.TryStartAsync(key, TimeSpan.FromSeconds(1)), Is.False);

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.That(await cooldowns.TryStartAsync(key, TimeSpan.FromSeconds(1)), Is.True);
    }

    [Test]
    public async Task Cooldown_OnlyOneOfManyConcurrentStartsWins()
    {
        var cooldowns = Services.GetRequiredService<ICooldownStore>();
        var key = "it-" + Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cooldowns.TryStartAsync(key, TimeSpan.FromMinutes(1))));

        Assert.That(results.Count(started => started), Is.EqualTo(1));
    }

    [Test]
    public async Task DeletingLinks_RemovesTheMappingTheUserListEntryAndTheStatistics()
    {
        var links = Services.GetRequiredService<IUrlMappingRepository>();
        var clicks = Services.GetRequiredService<IClickRepository>();
        var userId = NewUserId();
        var kept = new UrlMapping(NewShortCode(), "https://example.com/kept", userId, Now, Now.AddDays(1), IsCustom: false);
        var removed = new UrlMapping(NewShortCode(), "https://example.com/removed", userId, Now.AddMinutes(1), Now.AddDays(1), IsCustom: false);
        await links.TryInsertAsync(kept, 3600);
        await links.TryInsertAsync(removed, 3600);
        await clicks.AddClicksAsync(removed.ShortCode, 4, Now);

        await links.DeleteAsync(removed.ShortCode);
        await clicks.DeleteAsync(removed.ShortCode);

        Assert.That(await links.GetAsync(removed.ShortCode), Is.Null);
        Assert.That(await clicks.GetAsync(removed.ShortCode), Is.EqualTo(new ClickStats(0, null)));
        Assert.That(await links.GetAsync(kept.ShortCode), Is.EqualTo(kept));

        await links.DeleteUserListAsync(userId);
        Assert.That((await links.ListByUserAsync(userId, 10, pageToken: null)).Items, Is.Empty);
    }

    [Test]
    public async Task UrlCache_Remove_DeletesTheEntry_AndIgnoresAMissingOne()
    {
        var cache = Services.GetRequiredService<IUrlCache>();
        await cache.SetAsync("to-remove", new CachedUrl("https://example.com", Now.AddDays(1)), TimeSpan.FromMinutes(5));

        await cache.RemoveAsync("to-remove");
        await cache.RemoveAsync("never-cached");

        Assert.That(await cache.GetAsync("to-remove"), Is.Null);
    }
}
