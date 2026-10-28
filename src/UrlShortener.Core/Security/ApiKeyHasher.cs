using System.Security.Cryptography;
using System.Text;

namespace UrlShortener.Core.Security;

/// <summary>
/// Turns an API key into the value stored in the database. Only this hash is ever stored, so a leaked
/// database does not leak usable keys. Keys are long random strings, so a plain SHA-256 is sufficient
/// (a slow password hash is only needed for low-entropy secrets).
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Returns the lowercase hex SHA-256 of the key's UTF-8 bytes (the format used by users_by_api_key).</summary>
    public static string Hash(string apiKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }
}
