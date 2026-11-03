using Cassandra;
using Microsoft.Extensions.Logging;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed accounts (tables users, users_by_email and users_by_api_key).</summary>
public sealed class UserRepository(CassandraConnection connection, ILogger<UserRepository> logger) : IUserRepository
{
    public async Task<string?> GetUserIdByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectUserIdByKeyHash.Bind(apiKeyHash), cancellationToken);
        return result.FirstOrDefault()?.GetValue<string>("user_id");
    }

    public async Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectUserById.Bind(userId), cancellationToken);
        var row = result.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new UserAccount(
            userId,
            row.GetValue<string>("email"),
            row.GetValue<DateTimeOffset?>("created_at") ?? DateTimeOffset.UnixEpoch,
            row.GetValue<string?>("api_key_hash"));
    }

    public async Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectUserIdByEmail.Bind(email), cancellationToken);
        var userId = result.FirstOrDefault()?.GetValue<string>("user_id");
        return userId is null ? null : await GetByIdAsync(userId, cancellationToken);
    }

    public async Task<bool> TryCreateAsync(UserAccount user, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(user.ApiKeyHash);

        var context = await connection.GetContextAsync(cancellationToken);

        // Claiming the email first (an atomic check-and-write) is what guarantees one account per address.
        var claim = await context.ExecuteAsync(context.Statements.InsertUserEmail.Bind(user.Email, user.UserId), cancellationToken);
        if (!claim.First().GetValue<bool>("[applied]"))
        {
            return false;
        }

        try
        {
            await context.ExecuteAsync(
                context.Statements.InsertUser.Bind(user.UserId, user.Email, user.CreatedAt, user.ApiKeyHash),
                cancellationToken);
            await context.ExecuteAsync(context.Statements.InsertUserKey.Bind(user.ApiKeyHash, user.UserId), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without this the address would stay claimed by an account that does not exist.
            await TryReleaseAsync(context, user, ex);
            throw;
        }
    }

    public async Task ReplaceApiKeyAsync(
        string userId,
        string newApiKeyHash,
        string? presentedApiKeyHash,
        CancellationToken cancellationToken = default)
    {
        var current = await GetByIdAsync(userId, cancellationToken);
        var context = await connection.GetContextAsync(cancellationToken);

        // The new key goes in first: a failure part-way leaves the old key working, never a user with no key.
        await context.ExecuteAsync(context.Statements.InsertUserKey.Bind(newApiKeyHash, userId), cancellationToken);
        await context.ExecuteAsync(context.Statements.UpdateUserKeyHash.Bind(newApiKeyHash, userId), cancellationToken);

        foreach (var oldHash in new[] { current?.ApiKeyHash, presentedApiKeyHash }.Distinct())
        {
            if (oldHash is not null && oldHash != newApiKeyHash)
            {
                await context.ExecuteAsync(context.Statements.DeleteUserKey.Bind(oldHash), cancellationToken);
            }
        }
    }

    public async Task DeleteAsync(UserAccount user, string? presentedApiKeyHash, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        // Keys first so access is revoked at once; the email last, because a half-deleted account whose
        // address is still known can still be recovered and deleted again.
        foreach (var hash in new[] { user.ApiKeyHash, presentedApiKeyHash }.Distinct())
        {
            if (hash is not null)
            {
                await context.ExecuteAsync(context.Statements.DeleteUserKey.Bind(hash), cancellationToken);
            }
        }

        await context.ExecuteAsync(context.Statements.DeleteUser.Bind(user.UserId), cancellationToken);
        await context.ExecuteAsync(context.Statements.DeleteUserEmail.Bind(user.Email), cancellationToken);
    }

    private async Task TryReleaseAsync(CassandraContext context, UserAccount user, Exception cause)
    {
        try
        {
            await context.ExecuteAsync(context.Statements.DeleteUser.Bind(user.UserId), CancellationToken.None);
            await context.ExecuteAsync(context.Statements.DeleteUserEmail.Bind(user.Email), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Creating user {UserId} failed ({Cause}) and its email claim could not be released.", user.UserId, cause.GetType().Name);
        }
    }
}
