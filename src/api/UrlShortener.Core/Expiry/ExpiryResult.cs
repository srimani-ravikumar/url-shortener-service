namespace UrlShortener.Core.Expiry;

/// <summary>Outcome of resolving a link's expiry: the final UTC expiry time, or an error message.</summary>
public readonly record struct ExpiryResult(bool IsValid, DateTimeOffset ExpiresAt, string? Error)
{
    public static ExpiryResult Success(DateTimeOffset expiresAt) => new(true, expiresAt, null);

    public static ExpiryResult Fail(string error) => new(false, default, error);
}
