namespace UrlShortener.Infrastructure.Redis;

/// <summary>The one place that decides how Redis keys are named.</summary>
public sealed class RedisKeys(string prefix)
{
    /// <summary>The global ID counter.</summary>
    public string IdCounter => prefix + "id:counter";

    /// <summary>The set of short codes that have clicks waiting to be flushed to Cassandra.</summary>
    public string ChangedCodes => prefix + "clicks:dirty";

    /// <summary>Cached destination of a short link.</summary>
    public string Url(string shortCode) => $"{prefix}url:{shortCode}";

    /// <summary>Clicks on a link that have not been flushed yet.</summary>
    public string Clicks(string shortCode) => $"{prefix}clicks:{shortCode}";

    /// <summary>When a link was last clicked (Unix milliseconds), not yet flushed.</summary>
    public string LastAccess(string shortCode) => $"{prefix}last:{shortCode}";
}
