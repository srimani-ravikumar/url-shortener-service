using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests;

/// <summary>Tests the full counter -> short code pipeline: uniqueness, length and reversibility.</summary>
public class ShortCodeGeneratorTests
{
    private static readonly FeistelObfuscator Obfuscator = FeistelObfuscator.FromSecret("test-secret");

    [Test]
    public void Generate_ProducesUniqueBase62CodesOfAtMostSevenCharacters()
    {
        var generator = new ShortCodeGenerator(Obfuscator);
        var codes = new HashSet<string>();

        for (long counter = 1; counter <= 100_000; counter++)
        {
            var code = generator.Generate(counter);

            Assert.That(code, Has.Length.InRange(1, ShortCodeGenerator.MaxLength));
            Assert.That(Base62.TryDecode(code, out _), Is.True);
            Assert.That(codes.Add(code), Is.True, $"duplicate code at counter {counter}");
        }
    }

    [Test]
    public void Generate_CanBeReversedToTheCounterValue()
    {
        var generator = new ShortCodeGenerator(Obfuscator);

        foreach (var counter in new long[] { 0, 1, 1_000, 3_650_000_000, (long)FeistelObfuscator.MaxValue })
        {
            var decoded = Obfuscator.Deobfuscate(Base62.Decode(generator.Generate(counter)));

            Assert.That(decoded, Is.EqualTo((ulong)counter));
        }
    }

    [Test]
    public void Generate_RejectsValuesOutsideTheDomain()
    {
        var generator = new ShortCodeGenerator(Obfuscator);

        Assert.That(() => generator.Generate(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => generator.Generate((long)FeistelObfuscator.MaxValue + 1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
