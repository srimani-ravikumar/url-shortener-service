namespace UrlShortener.Core.Storage;

/// <summary>Looks up users by their API key.</summary>
public interface IUserRepository
{
    /// <summary>
    /// Returns the id of the user owning the key whose SHA-256 hash is given, or null if there is none.
    /// Only hashes are stored, never raw API keys.
    /// </summary>
    Task<string?> GetUserIdByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default);
}
