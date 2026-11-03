namespace UrlShortener.Core.Storage;

/// <summary>What an emailed token allows its holder to do.</summary>
public static class EmailTokenPurposes
{
    /// <summary>Confirm the address and create the account.</summary>
    public const string SignUp = "signup";

    /// <summary>Confirm the address and replace a lost API key.</summary>
    public const string Recover = "recover";
}

/// <summary>A pending email verification.</summary>
/// <param name="Purpose">One of <see cref="EmailTokenPurposes"/>.</param>
/// <param name="Email">The normalized address the token was sent to.</param>
/// <param name="CreatedAt">When the token was issued.</param>
public sealed record EmailToken(string Purpose, string Email, DateTimeOffset CreatedAt);

/// <summary>Single-use tokens that prove someone can read an email address. Only token hashes are stored.</summary>
public interface IEmailTokenStore
{
    /// <summary>Stores a token that expires on its own after <paramref name="lifetime"/>.</summary>
    Task SaveAsync(string tokenHash, EmailToken token, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the token and removes it, so it can be used once. Null if it is unknown, expired or already used.
    /// Of two simultaneous calls with the same token, only one gets it.
    /// </summary>
    Task<EmailToken?> TakeAsync(string tokenHash, CancellationToken cancellationToken = default);
}
