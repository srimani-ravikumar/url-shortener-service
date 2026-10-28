using System.Buffers;
using UrlShortener.Core.Validation;

namespace UrlShortener.Core.ShortCodes;

/// <summary>
/// The shape every short code has, generated or custom: 1 to 50 letters, digits, '-' or '_'.
/// The redirect service checks this first, so junk paths (favicon.ico, scanner probes) are answered
/// with 404 without ever touching the cache or the database.
/// </summary>
public static class ShortCodeFormat
{
    public const int MaxLength = CustomAliasValidator.MaxLength;

    private static readonly SearchValues<char> AllowedCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    /// <summary>True if the text could be a short code. It says nothing about whether such a link exists.</summary>
    public static bool IsValid(ReadOnlySpan<char> code) =>
        code.Length is >= 1 and <= MaxLength && !code.ContainsAnyExcept(AllowedCharacters);
}
