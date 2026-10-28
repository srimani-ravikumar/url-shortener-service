using Cassandra;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>
/// Every CQL statement the application runs, prepared once per connection.
/// Preparing up front means a typo in a query or a missing table fails at startup, not on first use.
/// </summary>
public sealed class CassandraStatements
{
    // The USING clause must come after IF NOT EXISTS in a conditional insert.
    private const string InsertMappingCql =
        "INSERT INTO url_mappings (short_code, long_url, user_id, created_at, expires_at, is_custom) " +
        "VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS USING TTL ?";

    private const string InsertUserUrlCql =
        "INSERT INTO urls_by_user (user_id, created_at, short_code, long_url, expires_at) " +
        "VALUES (?, ?, ?, ?, ?) USING TTL ?";

    private const string SelectMappingCql =
        "SELECT long_url, user_id, created_at, expires_at, is_custom FROM url_mappings WHERE short_code = ?";

    private const string SelectUserUrlsCql =
        "SELECT short_code, long_url, created_at, expires_at FROM urls_by_user WHERE user_id = ?";

    private const string SelectUserIdByKeyHashCql =
        "SELECT user_id FROM users_by_api_key WHERE api_key_hash = ?";

    private const string AddClicksCql =
        "UPDATE url_clicks SET click_count = click_count + ? WHERE short_code = ?";

    private const string UpsertLastAccessCql =
        "UPDATE url_last_access SET last_accessed = ? WHERE short_code = ?";

    private const string SelectClicksCql =
        "SELECT click_count FROM url_clicks WHERE short_code = ?";

    private const string SelectLastAccessCql =
        "SELECT last_accessed FROM url_last_access WHERE short_code = ?";

    private const string SelectCheckpointCql =
        "SELECT value FROM id_counter_checkpoint WHERE name = ?";

    private const string UpsertCheckpointCql =
        "INSERT INTO id_counter_checkpoint (name, value) VALUES (?, ?)";

    private CassandraStatements()
    {
    }

    public required PreparedStatement InsertMapping { get; init; }

    public required PreparedStatement InsertUserUrl { get; init; }

    public required PreparedStatement SelectMapping { get; init; }

    public required PreparedStatement SelectUserUrls { get; init; }

    public required PreparedStatement SelectUserIdByKeyHash { get; init; }

    public required PreparedStatement AddClicks { get; init; }

    public required PreparedStatement UpsertLastAccess { get; init; }

    public required PreparedStatement SelectClicks { get; init; }

    public required PreparedStatement SelectLastAccess { get; init; }

    public required PreparedStatement SelectCheckpoint { get; init; }

    public required PreparedStatement UpsertCheckpoint { get; init; }

    public static async Task<CassandraStatements> PrepareAsync(ISession session) => new()
    {
        InsertMapping = await session.PrepareAsync(InsertMappingCql),
        InsertUserUrl = await session.PrepareAsync(InsertUserUrlCql),
        SelectMapping = await session.PrepareAsync(SelectMappingCql),
        SelectUserUrls = await session.PrepareAsync(SelectUserUrlsCql),
        SelectUserIdByKeyHash = await session.PrepareAsync(SelectUserIdByKeyHashCql),
        AddClicks = await session.PrepareAsync(AddClicksCql),
        UpsertLastAccess = await session.PrepareAsync(UpsertLastAccessCql),
        SelectClicks = await session.PrepareAsync(SelectClicksCql),
        SelectLastAccess = await session.PrepareAsync(SelectLastAccessCql),
        SelectCheckpoint = await session.PrepareAsync(SelectCheckpointCql),
        UpsertCheckpoint = await session.PrepareAsync(UpsertCheckpointCql),
    };
}
