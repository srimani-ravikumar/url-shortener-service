using UrlShortener.Core.Expiry;

namespace UrlShortener.Core.Tests;

/// <summary>Tests expiry resolution and the row/cache TTL maths against a fake clock fixed at 2026-10-27 12:00 UTC.</summary>
public class ExpiryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private TestTimeProvider _time = null!;
    private ExpiryOptions _options = null!;

    // NUnit reuses one fixture instance for all tests, so the clock is rebuilt before every test.
    [SetUp]
    public void SetUp()
    {
        _time = new TestTimeProvider(Now);
        _options = new ExpiryOptions();
    }

    private ExpiryPolicy CreatePolicy() => new(_options, _time);

    [Test]
    public void Resolve_WithoutRequestedExpiry_UsesTheDefaultTtl()
    {
        var result = CreatePolicy().Resolve(null);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ExpiresAt, Is.EqualTo(Now.AddDays(365)));
    }

    [Test]
    public void Resolve_AcceptsFutureExpiry_AndNormalizesToUtc()
    {
        var requested = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(5.5));

        var result = CreatePolicy().Resolve(requested);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ExpiresAt, Is.EqualTo(requested));
        Assert.That(result.ExpiresAt.Offset, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Resolve_RejectsPastAndCurrentExpiry()
    {
        var policy = CreatePolicy();

        Assert.That(policy.Resolve(Now).IsValid, Is.False);
        Assert.That(policy.Resolve(Now.AddSeconds(-1)).IsValid, Is.False);
        Assert.That(policy.Resolve(Now.AddSeconds(1)).IsValid, Is.True);
    }

    [Test]
    public void Resolve_EnforcesTheMaximumLifetime()
    {
        var policy = CreatePolicy();

        Assert.That(policy.MaxLifetime, Is.EqualTo(TimeSpan.FromSeconds(630_720_000) - TimeSpan.FromDays(30)));
        Assert.That(policy.Resolve(Now + policy.MaxLifetime).IsValid, Is.True);

        var tooFar = policy.Resolve(Now + policy.MaxLifetime + TimeSpan.FromSeconds(1));
        Assert.That(tooFar.IsValid, Is.False);
        Assert.That(tooFar.Error, Does.Contain("days"));
    }

    [Test]
    public void GetRowTtlSeconds_AddsTheGracePeriod()
    {
        // 1 hour until expiry + the 30-day grace period.
        var expiresAt = Now.AddHours(1);

        Assert.That(CreatePolicy().GetRowTtlSeconds(expiresAt), Is.EqualTo(3600 + (30 * 24 * 3600)));
    }

    [Test]
    public void GetRowTtlSeconds_RoundsUp_AndNeverReturnsZero()
    {
        // No grace period, so the result depends only on the time until expiry.
        var policy = new ExpiryPolicy(_options with { GracePeriod = TimeSpan.Zero }, _time);

        Assert.That(policy.GetRowTtlSeconds(Now.AddMilliseconds(1)), Is.EqualTo(1));
        Assert.That(policy.GetRowTtlSeconds(Now.AddSeconds(10.2)), Is.EqualTo(11));
        Assert.That(policy.GetRowTtlSeconds(Now.AddDays(-1)), Is.EqualTo(1));
    }

    [Test]
    public void GetRowTtlSeconds_NeverExceedsTheCassandraMaximum()
    {
        var policy = CreatePolicy();
        var farthest = Now + policy.MaxLifetime;

        Assert.That(policy.GetRowTtlSeconds(farthest), Is.EqualTo(630_720_000));
        Assert.That(policy.GetRowTtlSeconds(farthest.AddYears(5)), Is.EqualTo(630_720_000));
    }

    [Test]
    public void GetCacheTtl_IsTheSmallerOfRemainingLifetimeAndTheDefault()
    {
        var policy = CreatePolicy();

        Assert.That(policy.GetCacheTtl(Now.AddMinutes(5)), Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(policy.GetCacheTtl(Now.AddDays(10)), Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public void GetCacheTtl_HonoursACustomCap()
    {
        var policy = CreatePolicy();

        Assert.That(policy.GetCacheTtl(Now.AddDays(10), TimeSpan.FromSeconds(60)), Is.EqualTo(TimeSpan.FromSeconds(60)));
        Assert.That(policy.GetCacheTtl(Now.AddSeconds(30), TimeSpan.FromSeconds(60)), Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void GetCacheTtl_IsZeroForExpiredLinks()
    {
        var policy = CreatePolicy();

        Assert.That(policy.GetCacheTtl(Now), Is.EqualTo(TimeSpan.Zero));
        Assert.That(policy.GetCacheTtl(Now.AddMinutes(-5)), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void IsExpired_ChangesAsTimePasses()
    {
        var policy = CreatePolicy();
        var expiresAt = Now.AddMinutes(5);

        Assert.That(policy.IsExpired(expiresAt), Is.False);

        _time.Now = expiresAt;
        Assert.That(policy.IsExpired(expiresAt), Is.True);
    }

    [Test]
    public void Constructor_RejectsInconsistentOptions()
    {
        Assert.That(() => new ExpiryPolicy(_options with { DefaultTtl = TimeSpan.Zero }), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new ExpiryPolicy(_options with { DefaultCacheTtl = TimeSpan.Zero }), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new ExpiryPolicy(_options with { GracePeriod = TimeSpan.FromDays(-1) }), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => new ExpiryPolicy(_options with { DefaultTtl = TimeSpan.FromDays(365 * 20) }), Throws.InstanceOf<ArgumentException>());
    }
}
