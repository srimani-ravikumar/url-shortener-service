using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests;

/// <summary>Tests which paths can possibly be short codes.</summary>
public class ShortCodeFormatTests
{
    [TestCase("0")]
    [TestCase("8zHKmQQ")]
    [TestCase("summer-sale")]
    [TestCase("Summer_Sale_2025")]
    public void IsValid_AcceptsGeneratedCodesAndAliases(string code)
    {
        Assert.That(ShortCodeFormat.IsValid(code), Is.True);
    }

    [Test]
    public void IsValid_AcceptsEveryGeneratedCode()
    {
        var generator = new ShortCodeGenerator(FeistelObfuscator.FromSecret("test-secret"));

        for (long counter = 1; counter <= 20_000; counter++)
        {
            Assert.That(ShortCodeFormat.IsValid(generator.Generate(counter)), Is.True);
        }
    }

    [Test]
    public void IsValid_EnforcesTheLengthLimits()
    {
        Assert.That(ShortCodeFormat.IsValid(string.Empty), Is.False);
        Assert.That(ShortCodeFormat.IsValid(new string('a', ShortCodeFormat.MaxLength)), Is.True);
        Assert.That(ShortCodeFormat.IsValid(new string('a', ShortCodeFormat.MaxLength + 1)), Is.False);
    }

    [TestCase("favicon.ico")]
    [TestCase("has space")]
    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase("caf\u00e9")]
    [TestCase("new\nline")]
    [TestCase("<script>")]
    public void IsValid_RejectsAnythingElse(string code)
    {
        Assert.That(ShortCodeFormat.IsValid(code), Is.False);
    }
}
