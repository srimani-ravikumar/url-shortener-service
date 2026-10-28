namespace UrlShortener.Redirector.Api.Configuration;

/// <summary>Settings of the redirect service (configuration section "Redirector").</summary>
internal sealed class RedirectorOptions
{
    public const string SectionName = "Redirector";

    /// <summary>
    /// The longest a browser or CDN may reuse a redirect without asking again (Cache-Control max-age).
    /// Kept short on purpose: while a redirect is cached downstream, the click is not counted and
    /// a change to the link is not seen. 0 turns downstream caching off.
    /// </summary>
    public int ClientCacheMaxAgeSeconds { get; set; } = 60;

    /// <summary>
    /// Adds an X-Served-By header with this instance's name (the container id in Docker) to every response, so a
    /// developer can see which replica answered. Off by default: it reveals internal details.
    /// </summary>
    public bool ExposeInstanceId { get; set; }
}
