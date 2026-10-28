using UrlShortener.Core.Security;

namespace UrlShortener.Core.Tests;

/// <summary>Tests that API keys are hashed the same way the database and the dev seed script expect.</summary>
public class ApiKeyHasherTests
{
    [Test]
    public void Hash_MatchesTheKnownSha256TestVector()
    {
        // SHA-256("abc") from FIPS 180-2, in lowercase hex.
        Assert.That(
            ApiKeyHasher.Hash("abc"),
            Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
    }

    [Test]
    public void Hash_IsDeterministic_AndDiffersPerKey()
    {
        var first = ApiKeyHasher.Hash("key-one");
        var again = ApiKeyHasher.Hash("key-one");

        Assert.That(first, Is.EqualTo(again));
        Assert.That(first, Is.Not.EqualTo(ApiKeyHasher.Hash("key-two")));
    }

    [Test]
    public void Hash_IsSixtyFourLowercaseHexCharacters()
    {
        Assert.That(ApiKeyHasher.Hash("any-key"), Does.Match("^[0-9a-f]{64}$"));
    }

    [TestCase(null)]
    [TestCase("")]
    public void Hash_RejectsAMissingKey(string? apiKey)
    {
        Assert.That(() => ApiKeyHasher.Hash(apiKey!), Throws.InstanceOf<ArgumentException>());
    }
}
