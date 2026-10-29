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

    // Accounts. The email claim is a conditional insert: it decides who owns an address.
    private const string InsertUserEmailCql =
        "INSERT INTO users_by_email (email, user_id) VALUES (?, ?) IF NOT EXISTS";

    private const string InsertUserCql =
        "INSERT INTO users (user_id, email, created_at, api_key_hash) VALUES (?, ?, ?, ?)";

    private const string InsertUserKeyCql =
        "INSERT INTO users_by_api_key (api_key_hash, user_id) VALUES (?, ?)";

    private const string SelectUserByIdCql =
        "SELECT email, created_at, api_key_hash FROM users WHERE user_id = ?";

    private const string SelectUserIdByEmailCql =
        "SELECT user_id FROM users_by_email WHERE email = ?";

    private const string UpdateUserKeyHashCql =
        "UPDATE users SET api_key_hash = ? WHERE user_id = ?";

    private const string DeleteUserKeyCql =
        "DELETE FROM users_by_api_key WHERE api_key_hash = ?";

    private const string DeleteUserEmailCql =
        "DELETE FROM users_by_email WHERE email = ?";

    private const string DeleteUserCql =
        "DELETE FROM users WHERE user_id = ?";

    // Email verification tokens (the row expires by itself).
    private const string InsertEmailTokenCql =
        "INSERT INTO email_tokens (token_hash, purpose, email, created_at) VALUES (?, ?, ?, ?) USING TTL ?";

    private const string SelectEmailTokenCql =
        "SELECT purpose, email, created_at FROM email_tokens WHERE token_hash = ?";

    // Conditional, so of two requests using the same token only one sees [applied] = true.
    private const string DeleteEmailTokenCql =
        "DELETE FROM email_tokens WHERE token_hash = ? IF EXISTS";

    // Removing links (account deletion).
    private const string DeleteMappingCql =
        "DELETE FROM url_mappings WHERE short_code = ?";

    private const string DeleteUserUrlsCql =
        "DELETE FROM urls_by_user WHERE user_id = ?";

    private const string DeleteClicksCql =
        "DELETE FROM url_clicks WHERE short_code = ?";

    private const string DeleteLastAccessCql =
        "DELETE FROM url_last_access WHERE short_code = ?";

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

    public required PreparedStatement InsertUserEmail { get; init; }

    public required PreparedStatement InsertUser { get; init; }

    public required PreparedStatement InsertUserKey { get; init; }

    public required PreparedStatement SelectUserById { get; init; }

    public required PreparedStatement SelectUserIdByEmail { get; init; }

    public required PreparedStatement UpdateUserKeyHash { get; init; }

    public required PreparedStatement DeleteUserKey { get; init; }

    public required PreparedStatement DeleteUserEmail { get; init; }

    public required PreparedStatement DeleteUser { get; init; }

    public required PreparedStatement InsertEmailToken { get; init; }

    public required PreparedStatement SelectEmailToken { get; init; }

    public required PreparedStatement DeleteEmailToken { get; init; }

    public required PreparedStatement DeleteMapping { get; init; }

    public required PreparedStatement DeleteUserUrls { get; init; }

    public required PreparedStatement DeleteClicks { get; init; }

    public required PreparedStatement DeleteLastAccess { get; init; }

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
        InsertUserEmail = await session.PrepareAsync(InsertUserEmailCql),
        InsertUser = await session.PrepareAsync(InsertUserCql),
        InsertUserKey = await session.PrepareAsync(InsertUserKeyCql),
        SelectUserById = await session.PrepareAsync(SelectUserByIdCql),
        SelectUserIdByEmail = await session.PrepareAsync(SelectUserIdByEmailCql),
        UpdateUserKeyHash = await session.PrepareAsync(UpdateUserKeyHashCql),
        DeleteUserKey = await session.PrepareAsync(DeleteUserKeyCql),
        DeleteUserEmail = await session.PrepareAsync(DeleteUserEmailCql),
        DeleteUser = await session.PrepareAsync(DeleteUserCql),
        InsertEmailToken = await session.PrepareAsync(InsertEmailTokenCql),
        SelectEmailToken = await session.PrepareAsync(SelectEmailTokenCql),
        DeleteEmailToken = await session.PrepareAsync(DeleteEmailTokenCql),
        DeleteMapping = await session.PrepareAsync(DeleteMappingCql),
        DeleteUserUrls = await session.PrepareAsync(DeleteUserUrlsCql),
        DeleteClicks = await session.PrepareAsync(DeleteClicksCql),
        DeleteLastAccess = await session.PrepareAsync(DeleteLastAccessCql),
    };
}
