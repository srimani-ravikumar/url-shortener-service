using UrlShortener.Core.Security;

namespace UrlShortener.Core.Tests;

/// <summary>Tests the random secrets used for API keys and email tokens.</summary>
public class SecretGeneratorTests
{
    [Test]
    public void NewSecret_IsFortyThreeUrlSafeCharacters()
    {
        // 32 bytes in Base64Url without padding.
        Assert.That(SecretGenerator.NewSecret(), Does.Match("^[A-Za-z0-9_-]{43}$"));
    }

    [Test]
    public void NewSecret_NeverRepeats()
    {
        var secrets = Enumerable.Range(0, 1000).Select(_ => SecretGenerator.NewSecret()).ToHashSet();

        Assert.That(secrets, Has.Count.EqualTo(1000));
    }
}
