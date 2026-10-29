using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Services;

/// <summary>How an account operation ended.</summary>
public enum AccountStatus
{
    /// <summary>The operation succeeded (for mail requests: it was accepted, whatever happened to the address).</summary>
    Ok,

    /// <summary>A request field is wrong; see the result's field and error.</summary>
    Invalid,

    /// <summary>The verification token is unknown, expired or already used.</summary>
    InvalidToken,

    /// <summary>Someone registered the address after the sign-up mail was sent.</summary>
    AlreadyRegistered,

    /// <summary>The account does not exist (any more).</summary>
    NotFound,

    /// <summary>The mail server did not take the message; retrying later may work.</summary>
    EmailUnavailable,
}

/// <summary>A newly issued API key. This is the only time the raw key exists outside the user's hands.</summary>
public sealed record IssuedCredentials(string UserId, string Email, string ApiKey);

/// <summary>
/// Outcome of an account operation. Expected failures are returned as values, so the controller can map each
/// one to the right HTTP status.
/// </summary>
public sealed record AccountResult
{
    /// <summary>What happened; decides which of the other properties are set.</summary>
    public required AccountStatus Status { get; init; }

    /// <summary>The request field at fault, e.g. "email" (only when Invalid).</summary>
    public string? Field { get; init; }

    /// <summary>A message safe to show to the caller (only when Invalid).</summary>
    public string? Error { get; init; }

    /// <summary>The new key (verify and rotate).</summary>
    public IssuedCredentials? Credentials { get; init; }

    /// <summary>The account (read).</summary>
    public UserAccount? Account { get; init; }

    /// <summary>Success without data.</summary>
    public static AccountResult Ok { get; } = new() { Status = AccountStatus.Ok };

    /// <summary>The account does not exist.</summary>
    public static AccountResult NotFound { get; } = new() { Status = AccountStatus.NotFound };

    /// <summary>The token is unknown, expired or used.</summary>
    public static AccountResult InvalidToken { get; } = new() { Status = AccountStatus.InvalidToken };

    /// <summary>The address got an account in the meantime.</summary>
    public static AccountResult AlreadyRegistered { get; } = new() { Status = AccountStatus.AlreadyRegistered };

    /// <summary>The mail could not be sent.</summary>
    public static AccountResult EmailUnavailable { get; } = new() { Status = AccountStatus.EmailUnavailable };

    /// <summary>A request field is wrong.</summary>
    public static AccountResult Invalid(string field, string error) =>
        new() { Status = AccountStatus.Invalid, Field = field, Error = error };

    /// <summary>Success carrying a new key.</summary>
    public static AccountResult WithCredentials(IssuedCredentials credentials) =>
        new() { Status = AccountStatus.Ok, Credentials = credentials };

    /// <summary>Success carrying the account.</summary>
    public static AccountResult WithAccount(UserAccount account) =>
        new() { Status = AccountStatus.Ok, Account = account };
}

/// <summary>Creating, recovering, rotating and deleting accounts.</summary>
public interface IAccountService
{
    /// <summary>
    /// Mails a verification link to a new address, or a "you already have an account" note to a known one.
    /// Answers Ok in both cases (and while a cooldown runs), so callers cannot tell which addresses are registered.
    /// </summary>
    Task<AccountResult> SignUpAsync(string? email, CancellationToken cancellationToken);

    /// <summary>Mails a recovery link if the address has an account; answers Ok either way.</summary>
    Task<AccountResult> RecoverAsync(string? email, CancellationToken cancellationToken);

    /// <summary>
    /// Spends an emailed token: creates the account (sign-up) or replaces the key (recovery), and returns the new key.
    /// </summary>
    Task<AccountResult> VerifyAsync(string? token, CancellationToken cancellationToken);

    /// <summary>The caller's own account.</summary>
    Task<AccountResult> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Issues a new key and revokes the old one.</summary>
    /// <param name="userId">The caller.</param>
    /// <param name="presentedApiKeyHash">Hash of the key used for this request; revoked with the recorded one.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<AccountResult> RotateKeyAsync(string userId, string presentedApiKeyHash, CancellationToken cancellationToken);

    /// <summary>Deletes the account and every link it owns.</summary>
    /// <param name="userId">The caller.</param>
    /// <param name="presentedApiKeyHash">Hash of the key used for this request; revoked with the recorded one.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    Task<AccountResult> DeleteAsync(string userId, string presentedApiKeyHash, CancellationToken cancellationToken);
}
