using UrlShortener.Core.ShortCodes;

namespace UrlShortener.Core.Tests;

/// <summary>Tests for Base62 encoding: known values, round-trips and rejection of bad input.</summary>
public class Base62Tests
{
    // The expected values are worked out by hand, e.g. 1000 = 16 * 62 + 8, and digit 16 is 'g'.
    [TestCase(0UL, "0")]
    [TestCase(9UL, "9")]
    [TestCase(10UL, "a")]
    [TestCase(35UL, "z")]
    [TestCase(36UL, "A")]
    [TestCase(61UL, "Z")]
    [TestCase(62UL, "10")]
    [TestCase(1_000UL, "g8")]
    [TestCase(1_000_000UL, "4c92")]
    [TestCase(1_000_000_000UL, "15FTGg")]
    public void Encode_ProducesExpectedValue(ulong value, string expected)
    {
        Assert.That(Base62.Encode(value), Is.EqualTo(expected));
        Assert.That(Base62.Decode(expected), Is.EqualTo(value));
    }

    [Test]
    public void RoundTrip_HoldsForBoundariesAndRandomValues()
    {
        var random = new Random(12345);
        var values = new List<ulong> { 0, 1, 61, 62, 3843, 3844, FeistelObfuscator.MaxValue, ulong.MaxValue };
        for (var i = 0; i < 10_000; i++)
        {
            values.Add((ulong)random.NextInt64() ^ (ulong)random.NextInt64());
        }

        foreach (var value in values)
        {
            Assert.That(Base62.Decode(Base62.Encode(value)), Is.EqualTo(value));
        }
    }

    [Test]
    public void Encode_Max40BitValue_FitsInSevenCharacters()
    {
        Assert.That(Base62.Encode(FeistelObfuscator.MaxValue), Has.Length.LessThanOrEqualTo(ShortCodeGenerator.MaxLength));
    }

    [TestCase("")]
    [TestCase("abc-123")]
    [TestCase("ab c")]
    [TestCase("é")]
    [TestCase("zzzzzzzzzzzzz")] // 13 digits: larger than ulong.MaxValue
    public void TryDecode_RejectsInvalidInput(string text)
    {
        Assert.That(Base62.TryDecode(text, out _), Is.False);
    }

    [Test]
    public void TryDecode_AcceptsUlongMaxValue_AndRejectsOneMore()
    {
        var max = Base62.Encode(ulong.MaxValue);

        Assert.That(Base62.TryDecode(max, out var value), Is.True);
        Assert.That(value, Is.EqualTo(ulong.MaxValue));

        // Appending a digit multiplies the value by 62, which must overflow.
        Assert.That(Base62.TryDecode(max[..^1] + "Z" + "0", out _), Is.False);
    }

    [Test]
    public void Decode_ThrowsOnInvalidInput()
    {
        Assert.That(() => Base62.Decode("not valid!"), Throws.TypeOf<FormatException>());
    }
}
