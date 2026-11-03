using Microsoft.Extensions.Options;
using UrlShortener.Core.Security;
using UrlShortener.Core.Storage;
using UrlShortener.Core.Validation;
using UrlShortener.Shortener.Api.Configuration;
using UrlShortener.Shortener.Api.Email;

namespace UrlShortener.Shortener.Api.Services;

/// <inheritdoc />
internal sealed class AccountService(
    IUserRepository users,
    IEmailTokenStore tokens,
    ICooldownStore cooldowns,
    IEmailSender mailer,
    IUrlMappingRepository links,
    IClickRepository clicks,
    IClickBuffer clickBuffer,
    IUrlCache cache,
    EmailAddressValidator emailValidator,
    TimeProvider time,
    IOptions<AccountOptions> options,
    ILogger<AccountService> logger) : IAccountService
{
    // Links are deleted in pages so a user with thousands of links never loads them all at once.
    private const int DeletePageSize = 100;

    // A token is 43 characters; anything far longer is garbage and is not hashed or looked up.
    private const int MaxTokenLength = 256;

    private readonly AccountOptions _options = options.Value;

    public async Task<AccountResult> SignUpAsync(string? email, CancellationToken cancellationToken)
    {
        var check = emailValidator.Validate(email, out var address);
        if (!check.IsValid)
        {
            return AccountResult.Invalid("email", check.Error!);
        }

        // Inside a cooldown nothing is sent but the answer is the same, so the caller learns nothing.
        if (!await StartMailCooldownAsync(address, cancellationToken))
        {
            return AccountResult.Ok;
        }

        var existing = await users.GetByEmailAsync(address, cancellationToken);

        try
        {
            if (existing is not null)
            {
                // Say so by mail instead of in the response: only the owner of the address may learn it.
                var (subject, body) = AccountMessages.AlreadyRegistered(WebLink("recover", token: null));
                await mailer.SendAsync(address, subject, body, cancellationToken);
            }
            else
            {
                var link = await IssueTokenAsync(EmailTokenPurposes.SignUp, address, cancellationToken);
                var (subject, body) = AccountMessages.SignUp(link, _options.TokenLifetime);
                await mailer.SendAsync(address, subject, body, cancellationToken);
            }
        }
        catch (EmailSendException)
        {
            return AccountResult.EmailUnavailable;
        }

        return AccountResult.Ok;
    }

    public async Task<AccountResult> RecoverAsync(string? email, CancellationToken cancellationToken)
    {
        var check = emailValidator.Validate(email, out var address);
        if (!check.IsValid)
        {
            return AccountResult.Invalid("email", check.Error!);
        }

        if (!await StartMailCooldownAsync(address, cancellationToken))
        {
            return AccountResult.Ok;
        }

        // An unknown address gets no mail, and the same answer as a known one.
        if (await users.GetByEmailAsync(address, cancellationToken) is null)
        {
            return AccountResult.Ok;
        }

        try
        {
            var link = await IssueTokenAsync(EmailTokenPurposes.Recover, address, cancellationToken);
            var (subject, body) = AccountMessages.Recover(link, _options.TokenLifetime);
            await mailer.SendAsync(address, subject, body, cancellationToken);
        }
        catch (EmailSendException)
        {
            return AccountResult.EmailUnavailable;
        }

        return AccountResult.Ok;
    }

    public async Task<AccountResult> VerifyAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return AccountResult.InvalidToken;
        }

        // Taking the token removes it, so a second use (or a race) gets nothing.
        var issued = await tokens.TakeAsync(ApiKeyHasher.Hash(token), cancellationToken);
        if (issued is null)
        {
            return AccountResult.InvalidToken;
        }

        return issued.Purpose switch
        {
            EmailTokenPurposes.SignUp => await CreateAccountAsync(issued.Email, cancellationToken),
            EmailTokenPurposes.Recover => await ReplaceLostKeyAsync(issued.Email, cancellationToken),
            _ => AccountResult.InvalidToken,
        };
    }

    public async Task<AccountResult> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var account = await users.GetByIdAsync(userId, cancellationToken);
        return account is null ? AccountResult.NotFound : AccountResult.WithAccount(account);
    }

    public async Task<AccountResult> RotateKeyAsync(string userId, string presentedApiKeyHash, CancellationToken cancellationToken)
    {
        var account = await users.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return AccountResult.NotFound;
        }

        var apiKey = SecretGenerator.NewSecret();
        await users.ReplaceApiKeyAsync(userId, ApiKeyHasher.Hash(apiKey), presentedApiKeyHash, cancellationToken);

        logger.LogInformation("User {UserId} rotated their API key.", userId);
        return AccountResult.WithCredentials(new IssuedCredentials(userId, account.Email, apiKey));
    }

    public async Task<AccountResult> DeleteAsync(string userId, string presentedApiKeyHash, CancellationToken cancellationToken)
    {
        var account = await users.GetByIdAsync(userId, cancellationToken);
        if (account is null)
        {
            return AccountResult.NotFound;
        }

        // Links first: until the very end the key still works, so a failure part-way can simply be retried.
        var deleted = await DeleteLinksAsync(userId, cancellationToken);
        await users.DeleteAsync(account, presentedApiKeyHash, cancellationToken);

        logger.LogInformation("Deleted user {UserId} and {LinkCount} links.", userId, deleted);
        return AccountResult.Ok;
    }

    private async Task<bool> StartMailCooldownAsync(string address, CancellationToken cancellationToken) =>
        // Hashed so the address itself never appears in Redis.
        await cooldowns.TryStartAsync("mail:" + ApiKeyHasher.Hash(address), _options.MailCooldown, cancellationToken);

    private async Task<Uri> IssueTokenAsync(string purpose, string address, CancellationToken cancellationToken)
    {
        var token = SecretGenerator.NewSecret();
        var record = new EmailToken(purpose, address, MillisecondsNow());
        await tokens.SaveAsync(ApiKeyHasher.Hash(token), record, _options.TokenLifetime, cancellationToken);

        // Sign-up and recovery links both land on the verify page; the token itself says which one it is.
        return WebLink("verify", token);
    }

    // The token travels in the URL fragment: browsers never send it to a server, so it stays out of logs and Referer headers.
    private Uri WebLink(string page, string? token)
    {
        var baseUrl = _options.WebBaseUrl.ToString().TrimEnd('/');
        return new Uri(token is null ? $"{baseUrl}/app/{page}" : $"{baseUrl}/app/{page}#token={token}");
    }

    private async Task<AccountResult> CreateAccountAsync(string address, CancellationToken cancellationToken)
    {
        var apiKey = SecretGenerator.NewSecret();
        var user = new UserAccount(Guid.NewGuid().ToString(), address, MillisecondsNow(), ApiKeyHasher.Hash(apiKey));

        if (!await users.TryCreateAsync(user, cancellationToken))
        {
            return AccountResult.AlreadyRegistered;
        }

        logger.LogInformation("Created user {UserId}.", user.UserId);
        return AccountResult.WithCredentials(new IssuedCredentials(user.UserId, address, apiKey));
    }

    private async Task<AccountResult> ReplaceLostKeyAsync(string address, CancellationToken cancellationToken)
    {
        var account = await users.GetByEmailAsync(address, cancellationToken);
        if (account is null)
        {
            // The account was deleted after the mail was sent.
            return AccountResult.InvalidToken;
        }

        var apiKey = SecretGenerator.NewSecret();
        await users.ReplaceApiKeyAsync(account.UserId, ApiKeyHasher.Hash(apiKey), presentedApiKeyHash: null, cancellationToken);

        logger.LogInformation("User {UserId} replaced a lost API key.", account.UserId);
        return AccountResult.WithCredentials(new IssuedCredentials(account.UserId, address, apiKey));
    }

    private async Task<int> DeleteLinksAsync(string userId, CancellationToken cancellationToken)
    {
        var count = 0;
        string? pageToken = null;

        do
        {
            var page = await links.ListByUserAsync(userId, DeletePageSize, pageToken, cancellationToken);

            foreach (var link in page.Items)
            {
                // The database row goes first and the cache entry after it, so nothing can put the link
                // back into the cache from a row that is already gone.
                await links.DeleteAsync(link.ShortCode, cancellationToken);
                await cache.RemoveAsync(link.ShortCode, cancellationToken);

                // Taking the unflushed clicks discards them; the statistics rows go last.
                await clickBuffer.TakeAsync(link.ShortCode, cancellationToken);
                await clicks.DeleteAsync(link.ShortCode, cancellationToken);
                count++;
            }

            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        await links.DeleteUserListAsync(userId, cancellationToken);
        return count;
    }

    // Cassandra stores milliseconds, so use the same precision everywhere.
    private DateTimeOffset MillisecondsNow() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
}
