namespace UrlShortener.Infrastructure.Redis;

/// <summary>Settings for the Redis connection (configuration section "Redis").</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Put in front of every key, so several environments (or tests) can share one Redis safely.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Redis sits on the redirect path, so a slow Redis must fail fast and let the database answer.</summary>
    public int OperationTimeoutMs { get; set; } = 500;

    public int ConnectTimeoutMs { get; set; } = 2000;
}
