using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>Runs the link repository against a real Cassandra.</summary>
public class UrlMappingRepositoryTests : IntegrationTestBase
{
    // Cassandra stores timestamps with millisecond precision, so test values are truncated to match.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private IUrlMappingRepository _repository = null!;

    [SetUp]
    public void SetUp() => _repository = Services.GetRequiredService<IUrlMappingRepository>();

    private UrlMapping NewMapping(string userId, DateTimeOffset? createdAt = null) => new(
        NewShortCode(),
        "https://example.com/some/long/url?x=1&y=2",
        userId,
        createdAt ?? Now,
        (createdAt ?? Now).AddDays(30),
        IsCustom: false);

    [Test]
    public async Task TryInsert_StoresTheLink_AndGetReturnsIt()
    {
        var mapping = NewMapping(NewUserId());

        var inserted = await _repository.TryInsertAsync(mapping, ttlSeconds: 3600);
        var loaded = await _repository.GetAsync(mapping.ShortCode);

        Assert.That(inserted, Is.True);
        Assert.That(loaded, Is.EqualTo(mapping));
    }

    [Test]
    public async Task TryInsert_ReturnsFalse_WhenTheCodeIsTaken_AndKeepsTheOriginal()
    {
        var original = NewMapping(NewUserId());
        await _repository.TryInsertAsync(original, ttlSeconds: 3600);

        var duplicate = original with { LongUrl = "https://example.com/other", UserId = NewUserId() };
        var inserted = await _repository.TryInsertAsync(duplicate, ttlSeconds: 3600);

        Assert.That(inserted, Is.False);
        Assert.That(await _repository.GetAsync(original.ShortCode), Is.EqualTo(original));
    }

    [Test]
    public async Task TryInsert_OnlyOneOfManyConcurrentClaimsWins()
    {
        var shortCode = NewShortCode();
        var attempts = Enumerable.Range(0, 20)
            .Select(i => _repository.TryInsertAsync(
                new UrlMapping(shortCode, $"https://example.com/{i}", NewUserId(), Now, Now.AddDays(1), IsCustom: true),
                ttlSeconds: 3600));

        var results = await Task.WhenAll(attempts);

        Assert.That(results.Count(applied => applied), Is.EqualTo(1));
    }

    [Test]
    public async Task TryInsert_AppliesTheTtl()
    {
        var mapping = NewMapping(NewUserId());
        await _repository.TryInsertAsync(mapping, ttlSeconds: 600);

        var rows = await ExecuteCqlAsync("SELECT TTL(long_url) AS ttl FROM url_mappings WHERE short_code = ?", mapping.ShortCode);

        Assert.That(rows.First().GetValue<int>("ttl"), Is.InRange(1, 600));
    }

    [Test]
    public async Task Get_ReturnsNull_ForAnUnknownCode()
    {
        Assert.That(await _repository.GetAsync(NewShortCode()), Is.Null);
    }

    [Test]
    public async Task TryInsert_AddsTheLinkToTheUsersList()
    {
        var userId = NewUserId();
        var mapping = NewMapping(userId);
        await _repository.TryInsertAsync(mapping, ttlSeconds: 3600);

        var page = await _repository.ListByUserAsync(userId, pageSize: 10, pageToken: null);

        Assert.That(page.Items, Has.Count.EqualTo(1));
        Assert.That(page.Items[0], Is.EqualTo(new UserUrl(mapping.ShortCode, mapping.LongUrl, mapping.CreatedAt, mapping.ExpiresAt)));
        Assert.That(page.NextPageToken, Is.Null);
    }

    [Test]
    public async Task ListByUser_ReturnsNewestFirst_AcrossPages()
    {
        var userId = NewUserId();
        var created = new List<UrlMapping>();
        for (var i = 0; i < 5; i++)
        {
            var mapping = NewMapping(userId, Now.AddMinutes(i));
            await _repository.TryInsertAsync(mapping, ttlSeconds: 3600);
            created.Add(mapping);
        }

        var pageSizes = new List<int>();
        var codes = new List<string>();
        string? token = null;
        do
        {
            var page = await _repository.ListByUserAsync(userId, pageSize: 2, token);
            pageSizes.Add(page.Items.Count);
            codes.AddRange(page.Items.Select(item => item.ShortCode));
            token = page.NextPageToken;
        }
        while (token is not null);

        Assert.That(pageSizes, Is.EqualTo(new[] { 2, 2, 1 }));
        Assert.That(codes, Is.EqualTo(created.AsEnumerable().Reverse().Select(m => m.ShortCode)));
    }

    [Test]
    public async Task ListByUser_ReturnsAnEmptyPage_ForAUserWithNoLinks()
    {
        var page = await _repository.ListByUserAsync(NewUserId(), pageSize: 10, pageToken: null);

        Assert.That(page.Items, Is.Empty);
        Assert.That(page.NextPageToken, Is.Null);
    }

    [Test]
    public async Task ListByUser_OnlyReturnsTheRequestedUsersLinks()
    {
        var owner = NewUserId();
        var other = NewUserId();
        await _repository.TryInsertAsync(NewMapping(owner), ttlSeconds: 3600);
        await _repository.TryInsertAsync(NewMapping(other), ttlSeconds: 3600);

        var page = await _repository.ListByUserAsync(owner, pageSize: 10, pageToken: null);

        Assert.That(page.Items, Has.Count.EqualTo(1));
    }

    [TestCase("not a token!!")]
    [TestCase("AQID")] // valid Base64Url, but not something Cassandra produced
    public async Task ListByUser_RejectsATokenThatWasNotIssuedByTheService(string token)
    {
        await Assert.ThatAsync(
            () => _repository.ListByUserAsync(NewUserId(), pageSize: 10, token),
            Throws.TypeOf<InvalidPageTokenException>());
    }

    [Test]
    public async Task ListByUser_RejectsAPageSizeBelowOne()
    {
        await Assert.ThatAsync(
            () => _repository.ListByUserAsync(NewUserId(), pageSize: 0, pageToken: null),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
