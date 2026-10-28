using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests;

/// <summary>Tests that the obfuscator is a reversible, collision-free, key-dependent permutation of 40-bit ids.</summary>
public class FeistelObfuscatorTests
{
    private static FeistelObfuscator Create(string secret = "test-secret") => FeistelObfuscator.FromSecret(secret);

    [Test]
    public void RoundTrip_HoldsForBoundariesAndRandomIds()
    {
        var obfuscator = Create();
        var random = new Random(42);
        var ids = new List<ulong> { 0, 1, 2, FeistelObfuscator.MaxValue - 1, FeistelObfuscator.MaxValue };
        for (var i = 0; i < 20_000; i++)
        {
            ids.Add((ulong)random.NextInt64(0, (long)FeistelObfuscator.MaxValue + 1));
        }

        foreach (var id in ids)
        {
            var obfuscated = obfuscator.Obfuscate(id);
            Assert.That(obfuscated, Is.LessThanOrEqualTo(FeistelObfuscator.MaxValue));
            Assert.That(obfuscator.Deobfuscate(obfuscated), Is.EqualTo(id));
        }
    }

    [Test]
    public void Obfuscate_IsCollisionFree_OverSequentialRange()
    {
        // A Feistel network is a permutation by construction; this guards against implementation mistakes.
        var obfuscator = Create();
        var seen = new HashSet<ulong>();

        for (ulong id = 1; id <= 300_000; id++)
        {
            Assert.That(seen.Add(obfuscator.Obfuscate(id)), Is.True, $"collision at id {id}");
        }
    }

    [Test]
    public void Obfuscate_IsDeterministic_ForTheSameSecret()
    {
        var first = Create("same-secret");
        var second = Create("same-secret");

        Assert.That(first.Obfuscate(123_456), Is.EqualTo(second.Obfuscate(123_456)));
    }

    [Test]
    public void Obfuscate_DiffersBetweenSecrets()
    {
        // Allow a handful of coincidental matches between two different keys.
        var differing = Enumerable.Range(1, 1_000)
            .Count(id => Create("secret-a").Obfuscate((ulong)id) != Create("secret-b").Obfuscate((ulong)id));

        Assert.That(differing, Is.GreaterThan(990));
    }

    [Test]
    public void Obfuscate_DoesNotPreserveSequentialOrder()
    {
        // The point of obfuscation: consecutive ids must not give consecutive codes.
        var obfuscator = Create();
        var adjacentIncrements = Enumerable.Range(1, 1_000)
            .Count(id => obfuscator.Obfuscate((ulong)id + 1) == obfuscator.Obfuscate((ulong)id) + 1);

        Assert.That(adjacentIncrements, Is.EqualTo(0));
    }

    [Test]
    public void Obfuscate_RejectsIdsOutsideTheDomain()
    {
        var obfuscator = Create();

        Assert.That(() => obfuscator.Obfuscate(FeistelObfuscator.MaxValue + 1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => obfuscator.Deobfuscate(FeistelObfuscator.MaxValue + 1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Constructor_RejectsTooFewRounds()
    {
        Assert.That(() => new FeistelObfuscator(1, rounds: 2), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void FromSecret_RejectsBlankSecrets(string? secret)
    {
        Assert.That(() => FeistelObfuscator.FromSecret(secret!), Throws.InstanceOf<ArgumentException>());
    }
}
