namespace UrlShortener.Shortener.Api.Configuration;

/// <summary>Names of rate limiting policies, shared by their registration and the controllers that use them.</summary>
internal static class RateLimiting
{
    /// <summary>Applied to link creation.</summary>
    public const string ShortenPolicy = "shorten";
}
