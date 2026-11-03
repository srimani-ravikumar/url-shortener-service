using System.Buffers.Text;
using System.Security.Cryptography;

namespace UrlShortener.Core.Security;

/// <summary>Creates the random secrets handed to users: API keys and email verification tokens.</summary>
public static class SecretGenerator
{
    // 32 random bytes = 256 bits, far beyond guessing; Base64Url makes it safe in headers and URLs (43 characters).
    private const int SecretBytes = 32;

    /// <summary>A new unguessable secret. Store only <see cref="ApiKeyHasher.Hash"/> of it.</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));
}
