using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// Reversible keyed permutation over a 40-bit domain, so sequential counter values map to
/// unguessable-looking, collision-free values that still fit in at most 7 Base62 characters.
/// This is obfuscation, not encryption.
/// </summary>
/// <remarks>
/// How a Feistel network works: split the 40 bits into a left and right half of 20 bits each.
/// Every round, the new left half is the old right half, and the new right half is the old left half
/// XORed with a scrambled copy of the old right half. Because XOR can be undone, every round can be
/// reversed no matter how the scrambling function behaves, so two different inputs never collide.
/// </remarks>
public sealed class FeistelObfuscator
{
    public const int DomainBits = 40;
    public const ulong MaxValue = (1UL << DomainBits) - 1;

    // With fewer rounds the output still shows a visible pattern.
    public const int MinRounds = 3;

    private const int HalfBits = DomainBits / 2;
    private const ulong HalfMask = (1UL << HalfBits) - 1;

    // One derived key per round, so each round scrambles differently.
    private readonly ulong[] _roundKeys;

    /// <param name="key">Secret that decides the permutation. Changing it changes every output.</param>
    /// <param name="rounds">Number of Feistel rounds (at least 3).</param>
    public FeistelObfuscator(ulong key, int rounds = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, MinRounds);

        // Expand the single key into one well-mixed key per round.
        _roundKeys = new ulong[rounds];
        var state = key;
        for (var i = 0; i < rounds; i++)
        {
            _roundKeys[i] = SplitMix64(ref state);
        }
    }

    /// <summary>Creates an obfuscator from a configuration secret of any length.</summary>
    public static FeistelObfuscator FromSecret(string secret, int rounds = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // Hash the text secret down to the 64-bit key the constructor needs.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return new FeistelObfuscator(BinaryPrimitives.ReadUInt64LittleEndian(hash), rounds);
    }

    /// <summary>Maps an id in 0 to 2^40 - 1 to a different value in the same range.</summary>
    public ulong Obfuscate(ulong id)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, MaxValue);

        var left = id >> HalfBits;
        var right = id & HalfMask;
        foreach (var roundKey in _roundKeys)
        {
            // New left = old right; new right = old left XOR scrambled(old right).
            (left, right) = (right, left ^ RoundFunction(right, roundKey));
        }

        return (left << HalfBits) | right;
    }

    /// <summary>Reverses <see cref="Obfuscate"/>, returning the original id.</summary>
    public ulong Deobfuscate(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxValue);

        var left = value >> HalfBits;
        var right = value & HalfMask;

        // Undo the rounds in reverse order. Since the forward step set new left = old right,
        // the old right is available as the current left, so the scramble can be recomputed and XORed away.
        for (var i = _roundKeys.Length - 1; i >= 0; i--)
        {
            (left, right) = (right ^ RoundFunction(left, _roundKeys[i]), left);
        }

        return (left << HalfBits) | right;
    }

    // The scrambling step: mixes one 20-bit half with a round key and keeps 20 bits.
    // It does not need to be reversible, which is what makes the Feistel structure convenient.
    private static ulong RoundFunction(ulong half, ulong roundKey)
    {
        var state = half + roundKey;
        return SplitMix64(ref state) & HalfMask;
    }

    // SplitMix64: a small, fast 64-bit mixing function (advances the state and returns a scrambled value).
    private static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
