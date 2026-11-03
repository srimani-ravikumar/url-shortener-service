namespace UrlShortener.Shortener.Api.Configuration;

/// <summary>Names of rate limiting policies, shared by their registration and the controllers that use them.</summary>
internal static class RateLimiting
{
    /// <summary>Applied to link creation.</summary>
    public const string ShortenPolicy = "shorten";

    /// <summary>Applied to sign-up and recovery requests (each sends a mail).</summary>
    public const string AccountMailPolicy = "account-mail";

    /// <summary>Applied to spending an emailed token.</summary>
    public const string AccountVerifyPolicy = "account-verify";
}
