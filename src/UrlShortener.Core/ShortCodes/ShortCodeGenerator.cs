namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// Turns a unique counter value (from the Redis INCR counter) into a short code:
/// counter -> Feistel obfuscation -> Base62. Uniqueness comes from the counter; the obfuscation
/// only hides the sequence so codes can't be guessed by incrementing.
/// </summary>
public sealed class ShortCodeGenerator(FeistelObfuscator obfuscator)
{
    // 62^7 > 2^40, so every obfuscated value fits in 7 Base62 characters.
    public const int MaxLength = 7;

    /// <summary>Generates the short code for a counter value in the range 0 to 2^40 - 1.</summary>
    public string Generate(long counterValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(counterValue);
        return Base62.Encode(obfuscator.Obfuscate((ulong)counterValue));
    }
}
