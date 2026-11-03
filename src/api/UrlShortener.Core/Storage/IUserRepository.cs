namespace UrlShortener.Core.Storage;

/// <summary>An account as stored in the database.</summary>
/// <param name="UserId">The user's id.</param>
/// <param name="Email">Normalized (trimmed, lower-case) address; unique across accounts.</param>
/// <param name="CreatedAt">When the account was created.</param>
/// <param name="ApiKeyHash">SHA-256 of the user's current key; null for accounts created before it was recorded.</param>
public sealed record UserAccount(string UserId, string Email, DateTimeOffset CreatedAt, string? ApiKeyHash);

/// <summary>Reads and writes users and their API keys. Only key hashes are stored, never raw keys.</summary>
public interface IUserRepository
{
    /// <summary>
    /// Returns the id of the user owning the key whose SHA-256 hash is given, or null if there is none.
    /// </summary>
    Task<string?> GetUserIdByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default);

    /// <summary>Finds an account by user id, or null.</summary>
    Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Finds an account by normalized email, or null.</summary>
    Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the account and its key. Returns false, creating nothing, when the email already has an account.
    /// </summary>
    Task<bool> TryCreateAsync(UserAccount user, CancellationToken cancellationToken = default);

    /// <summary>Gives the user a new key and revokes the old one.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="newApiKeyHash">Hash of the new key.</param>
    /// <param name="presentedApiKeyHash">
    /// Hash of the key the caller used, if any; revoked too, in case it is not the one recorded on the account.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task ReplaceApiKeyAsync(
        string userId,
        string newApiKeyHash,
        string? presentedApiKeyHash,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the account, its email claim and its key(s). Does not touch the user's links.</summary>
    /// <param name="user">The account to delete.</param>
    /// <param name="presentedApiKeyHash">Hash of the key the caller used, if any; revoked as well.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task DeleteAsync(UserAccount user, string? presentedApiKeyHash, CancellationToken cancellationToken = default);
}
