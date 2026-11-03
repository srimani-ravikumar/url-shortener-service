namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Settings for the Cassandra connection (configuration section "Cassandra").</summary>
public sealed class CassandraOptions
{
    public const string SectionName = "Cassandra";

    /// <summary>Comma-separated host names; one reachable node is enough, the driver discovers the rest.</summary>
    public string ContactPoints { get; set; } = "localhost";

    public int Port { get; set; } = 9042;

    public string Keyspace { get; set; } = "urlshortener";

    /// <summary>How long to wait for a single query before giving up.</summary>
    public int QueryTimeoutMs { get; set; } = 2000;

    public int ConnectTimeoutMs { get; set; } = 5000;
}
