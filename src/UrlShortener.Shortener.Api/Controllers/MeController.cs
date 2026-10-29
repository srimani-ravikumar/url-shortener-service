using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Shortener.Api.Authentication;
using UrlShortener.Shortener.Api.Models;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Controllers;

/// <summary>The caller's own account: read it, replace its API key, delete it.</summary>
[ApiController]
[Authorize]
[Route("api/v1/users/me")]
public sealed class MeController(IAccountService accounts) : ControllerBase
{
    /// <summary>Returns your account. Also the way to check that an API key works.</summary>
    /// <response code="200">Your account.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    [HttpGet(Name = "GetMe")]
    [ProducesResponseType<AccountResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AccountResponse>> Get(CancellationToken cancellationToken)
    {
        var result = await accounts.GetAsync(User.GetUserId(), cancellationToken);
        if (result.Status != AccountStatus.Ok)
        {
            return Unauthorized();
        }

        var account = result.Account!;
        return new AccountResponse(account.UserId, account.Email, account.CreatedAt);
    }

    /// <summary>Issues a new API key and revokes the one you are using.</summary>
    /// <response code="200">The new key; store it now, it is not shown again.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="503">Storage is temporarily unavailable; retry shortly.</response>
    [HttpPost("api-key/rotate", Name = "RotateApiKey")]
    [ProducesResponseType<CredentialsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CredentialsResponse>> Rotate(CancellationToken cancellationToken)
    {
        var result = await accounts.RotateKeyAsync(User.GetUserId(), User.GetApiKeyHash(), cancellationToken);
        if (result.Status != AccountStatus.Ok)
        {
            return Unauthorized();
        }

        // The response holds a secret; no cache or proxy may keep it.
        Response.Headers.CacheControl = "no-store";
        var credentials = result.Credentials!;
        return new CredentialsResponse(credentials.UserId, credentials.Email, credentials.ApiKey);
    }

    /// <summary>Deletes your account and every link you created. Cannot be undone.</summary>
    /// <remarks>Deleted links stop redirecting (404). If this fails part-way, call it again.</remarks>
    /// <response code="204">The account and its links are gone.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="503">Storage is temporarily unavailable; retry shortly.</response>
    [HttpDelete(Name = "DeleteMe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        var result = await accounts.DeleteAsync(User.GetUserId(), User.GetApiKeyHash(), cancellationToken);
        return result.Status == AccountStatus.Ok ? NoContent() : Unauthorized();
    }
}
