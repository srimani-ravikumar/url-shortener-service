using System.Text.RegularExpressions;

namespace UrlShortener.Core.Validation;

/// <summary>
/// Checks and normalizes the email address of an account. This is a sanity check, not full RFC 5322:
/// whether the address really exists is proven by the verification mail.
/// </summary>
public sealed partial class EmailAddressValidator
{
    /// <summary>The longest address the standard allows.</summary>
    public const int MaxLength = 254;

    /// <summary>
    /// Validates an address and returns its normalized form (trimmed, lower-case), which is the form
    /// stored and compared. The error message on failure is safe to return to the caller.
    /// </summary>
    public ValidationResult Validate(string? email, out string normalized)
    {
        normalized = string.Empty;

        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ValidationResult.Fail("email must not be empty.");
        }

        if (trimmed.Length > MaxLength)
        {
            return ValidationResult.Fail($"email must be at most {MaxLength} characters.");
        }

        if (!Shape().IsMatch(trimmed))
        {
            return ValidationResult.Fail("email is not a valid address.");
        }

        normalized = trimmed.ToLowerInvariant();
        return ValidationResult.Success;
    }

    // One '@', a local part of up to 64 characters, and a dotted domain; no spaces or control characters.
    // \z (not $) so a trailing newline can't slip through.
    [GeneratedRegex(@"^[^@\s\p{C}]{1,64}@[^@\s\p{C}.]+(\.[^@\s\p{C}.]+)+\z")]
    private static partial Regex Shape();
}
