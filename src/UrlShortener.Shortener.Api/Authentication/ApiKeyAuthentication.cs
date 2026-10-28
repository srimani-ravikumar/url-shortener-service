using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Security;
using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Authentication;

/// <summary>Names used by API key authentication.</summary>
internal static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    // Keys are ~43 characters; anything far longer is garbage, so don't bother hashing or querying it.
    public const int MaxKeyLength = 256;
}

/// <summary>
/// Authenticates a request by its X-Api-Key header. The key is hashed and looked up in the database;
/// the owning user's id becomes the caller's identity, so it can never be supplied by the client directly.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IUserRepository users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header: not an authentication failure, just anonymous (the authorization policy then answers 401).
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var header) || header.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = header.ToString();
        if (header.Count > 1 || string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > ApiKeyDefaults.MaxKeyLength)
        {
            return AuthenticateResult.Fail("Malformed API key.");
        }

        var userId = await users.GetUserIdByApiKeyHashAsync(ApiKeyHasher.Hash(apiKey), Context.RequestAborted);
        if (userId is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A 401 must say how to authenticate; the body is filled in as a problem document by the status code pages.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ApiKeyDefaults.Scheme;
        return Task.CompletedTask;
    }
}

/// <summary>Reads the caller's identity from a request.</summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>The id of the authenticated user. Only call from endpoints that require authentication.</summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The request is not authenticated.");
}
