using Cassandra;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed user lookup (table users_by_api_key).</summary>
public sealed class UserRepository(CassandraConnection connection) : IUserRepository
{
    public async Task<string?> GetUserIdByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectUserIdByKeyHash.Bind(apiKeyHash), cancellationToken);
        return result.FirstOrDefault()?.GetValue<string>("user_id");
    }
}
