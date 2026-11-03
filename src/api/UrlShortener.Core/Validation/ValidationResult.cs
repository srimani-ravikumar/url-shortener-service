namespace UrlShortener.Core.Validation;

/// <summary>Outcome of a validation check: success, or failure with a message safe to return to the caller.</summary>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Success { get; } = new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}
