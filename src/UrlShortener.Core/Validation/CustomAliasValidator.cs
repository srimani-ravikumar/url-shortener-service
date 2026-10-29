using System.Text.RegularExpressions;

namespace UrlShortener.Core.Validation;

/// <summary>
/// Checks a user-chosen alias (the short code in a link like /summer-sale) for length, allowed
/// characters and reserved words. Aliases are case-sensitive: "Sale" and "sale" are different codes.
/// Uniqueness is NOT checked here; it is enforced by the database insert.
/// </summary>
public sealed partial class CustomAliasValidator
{
    public const int MinLength = 3;
    public const int MaxLength = 50;

    // Words that would clash with application routes or look official; matched ignoring case.
    private static readonly string[] DefaultReserved =
    [
        "api", "admin", "login", "logout", "help", "contact", "shorten",
        "analytics", "users", "health", "swagger", "openapi", "metrics", "v1",
        "app", "signup", "recover", "verify",
    ];

    private readonly HashSet<string> _reserved;

    /// <param name="additionalReserved">Extra blocked words (e.g. an offensive-word list) on top of the built-in ones.</param>
    public CustomAliasValidator(IEnumerable<string>? additionalReserved = null)
    {
        _reserved = new HashSet<string>(DefaultReserved, StringComparer.OrdinalIgnoreCase);
        if (additionalReserved is not null)
        {
            _reserved.UnionWith(additionalReserved);
        }
    }

    /// <summary>Validates an alias; the error message on failure is safe to return to the caller.</summary>
    public ValidationResult Validate(string? alias)
    {
        if (string.IsNullOrEmpty(alias))
        {
            return ValidationResult.Fail("custom_alias must not be empty.");
        }

        if (alias.Length is < MinLength or > MaxLength)
        {
            return ValidationResult.Fail($"custom_alias must be {MinLength}-{MaxLength} characters.");
        }

        if (!AllowedCharacters().IsMatch(alias))
        {
            return ValidationResult.Fail("custom_alias may only contain letters, digits, '-' and '_'.");
        }

        if (_reserved.Contains(alias))
        {
            return ValidationResult.Fail("custom_alias is reserved.");
        }

        return ValidationResult.Success;
    }

    // \z (not $) so a trailing newline can't slip through.
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+\z")]
    private static partial Regex AllowedCharacters();
}
