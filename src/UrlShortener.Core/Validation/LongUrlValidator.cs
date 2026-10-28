namespace UrlShortener.Core.Validation;

/// <summary>
/// Checks that a destination URL is safe and sensible to store: a well-formed absolute http(s) URL
/// of reasonable length that does not point back at this service (which would create a redirect loop).
/// </summary>
public sealed class LongUrlValidator
{
    public const int DefaultMaxLength = 2048;

    private readonly string _shortHost;
    private readonly int _maxLength;

    /// <param name="shortUrlBase">Public base URL of the short link service, e.g. http://short.url.</param>
    /// <param name="maxLength">Longest accepted URL, in characters.</param>
    public LongUrlValidator(Uri shortUrlBase, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(shortUrlBase);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        _shortHost = NormalizeHost(shortUrlBase);
        _maxLength = maxLength;
    }

    /// <summary>Validates a long URL; the error message on failure is safe to return to the caller.</summary>
    public ValidationResult Validate(string? longUrl)
    {
        if (string.IsNullOrWhiteSpace(longUrl))
        {
            return ValidationResult.Fail("long_url is required.");
        }

        if (longUrl.Length > _maxLength)
        {
            return ValidationResult.Fail($"long_url must be at most {_maxLength} characters.");
        }

        // The URL is stored exactly as given, so surrounding whitespace is rejected instead of silently trimmed.
        if (longUrl.Trim().Length != longUrl.Length)
        {
            return ValidationResult.Fail("long_url must not have leading or trailing whitespace.");
        }

        if (!Uri.TryCreate(longUrl, UriKind.Absolute, out var uri))
        {
            return ValidationResult.Fail("long_url must be an absolute URL.");
        }

        // Only web links: blocks javascript:, file:, ftp:, mailto: and similar schemes.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ValidationResult.Fail("long_url must use http or https.");
        }

        // A link to ourselves would redirect to itself (or bounce between short links) forever.
        if (NormalizeHost(uri) == _shortHost)
        {
            return ValidationResult.Fail("long_url must not point to the short URL service.");
        }

        return ValidationResult.Success;
    }

    // Host comparison ignores case, a trailing dot ("example.com.") and internationalized-name encoding.
    private static string NormalizeHost(Uri uri) => uri.IdnHost.TrimEnd('.').ToLowerInvariant();
}
