using System.ComponentModel.DataAnnotations;

namespace UrlShortener.Shortener.Api.Configuration;

/// <summary>Settings of the shortener service (configuration section "Shortener").</summary>
internal sealed class ShortenerOptions
{
    public const string SectionName = "Shortener";

    /// <summary>Public address of the redirect service; short links are built as {ShortUrlBase}/{code}.</summary>
    [Required]
    public Uri ShortUrlBase { get; set; } = null!;

    /// <summary>
    /// Secret that scrambles the ID counter into short codes. Keep it constant for the life of the data,
    /// and keep it out of source control: supply it through an environment variable in real deployments.
    /// </summary>
    [Required, MinLength(16)]
    public string FeistelSecret { get; set; } = string.Empty;

    /// <summary>How often to try again with a fresh id if a generated code turns out to be taken.</summary>
    [Range(1, 10)]
    public int MaxGenerateAttempts { get; set; } = 3;
}

/// <summary>
/// Limits how fast one API key may create links (configuration section "RateLimit").
/// A token bucket allows short bursts while enforcing an average rate:
/// the defaults refill 10 tokens every 6 seconds, which is 100 per minute.
/// </summary>
internal sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>Bucket size: the largest burst a key can send at once.</summary>
    [Range(1, 100_000)]
    public int TokenLimit { get; set; } = 100;

    [Range(1, 100_000)]
    public int TokensPerPeriod { get; set; } = 10;

    [Range(1, 3600)]
    public int ReplenishmentPeriodSeconds { get; set; } = 6;
}
