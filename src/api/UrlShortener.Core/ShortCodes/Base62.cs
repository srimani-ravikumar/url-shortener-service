namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// Converts unsigned integers to and from Base62 text (digits, then lowercase, then uppercase).
/// Example: 1000 becomes "g8" because 1000 = 16 * 62 + 8, and digit 16 is 'g'.
/// </summary>
public static class Base62
{
    // Digit value = position in this string: '0'-'9' are 0-9, 'a'-'z' are 10-35, 'A'-'Z' are 36-61.
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int Radix = 62;

    // ceil(64 / log2(62)) digits are enough for any ulong.
    private const int MaxDigits = 11;

    /// <summary>Encodes a number as Base62 text. Zero encodes as "0".</summary>
    public static string Encode(ulong value)
    {
        if (value == 0)
        {
            return "0";
        }

        // Fill the buffer from the right: the remainder is the least significant digit.
        Span<char> buffer = stackalloc char[MaxDigits];
        var index = buffer.Length;
        while (value > 0)
        {
            buffer[--index] = Alphabet[(int)(value % Radix)];
            value /= Radix;
        }

        return new string(buffer[index..]);
    }

    /// <summary>
    /// Decodes Base62 text. Returns false for empty text, characters outside the alphabet,
    /// or values that do not fit in a ulong.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        ulong result = 0;
        foreach (var c in text)
        {
            var digit = DigitOf(c);

            // Reject unknown characters, and reject up front when result * 62 + digit would overflow.
            if (digit < 0 || result > (ulong.MaxValue - (ulong)digit) / Radix)
            {
                return false;
            }

            result = (result * Radix) + (ulong)digit;
        }

        value = result;
        return true;
    }

    /// <summary>Decodes Base62 text, throwing <see cref="FormatException"/> if it is invalid.</summary>
    public static ulong Decode(string text) =>
        TryDecode(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a valid Base62 value.");

    // Returns the digit value of a character, or -1 if it is not in the alphabet.
    private static int DigitOf(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => 10 + (c - 'a'),
        >= 'A' and <= 'Z' => 36 + (c - 'A'),
        _ => -1,
    };
}
