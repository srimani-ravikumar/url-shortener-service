using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Shortener.Api.Configuration;
using UrlShortener.Shortener.Api.Models;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Controllers;

/// <summary>Getting an API key: sign up, recover a lost key and spend the emailed link. Needs no key.</summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1")]
public sealed class RegistrationController(IAccountService accounts) : ControllerBase
{
    /// <summary>Asks for an email with a link that creates an account.</summary>
    /// <remarks>
    /// The answer is the same whether or not the address already has an account (an account holder is told
    /// by mail instead), so addresses cannot be probed. At most one mail per address per minute is sent.
    /// </remarks>
    /// <response code="202">Accepted; if the address is valid a mail is on its way.</response>
    /// <response code="400">The address is not valid.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The mail could not be sent; try again shortly.</response>
    [HttpPost("signup", Name = "SignUp")]
    [EnableRateLimiting(RateLimiting.AccountMailPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SignUp(EmailRequest request, CancellationToken cancellationToken) =>
        MailRequestResponse(await accounts.SignUpAsync(request.Email, cancellationToken));

    /// <summary>Asks for an email with a link that replaces a lost API key.</summary>
    /// <remarks>Like sign-up, the answer does not reveal whether the address has an account.</remarks>
    /// <response code="202">Accepted; if the address has an account a mail is on its way.</response>
    /// <response code="400">The address is not valid.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">The mail could not be sent; try again shortly.</response>
    [HttpPost("recover", Name = "RecoverKey")]
    [EnableRateLimiting(RateLimiting.AccountMailPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Recover(EmailRequest request, CancellationToken cancellationToken) =>
        MailRequestResponse(await accounts.RecoverAsync(request.Email, cancellationToken));

    /// <summary>Spends the token from an emailed link and returns the API key.</summary>
    /// <remarks>
    /// A sign-up token creates the account; a recovery token replaces the lost key (the old key stops working).
    /// The key is shown only here, once. A token works once.
    /// </remarks>
    /// <response code="200">The account's API key; store it now.</response>
    /// <response code="400">The token is invalid, expired or already used.</response>
    /// <response code="409">The address got an account while the mail was on its way.</response>
    /// <response code="429">Too many requests from this address.</response>
    /// <response code="503">Storage is temporarily unavailable; retry shortly.</response>
    [HttpPost("verify", Name = "VerifyEmail")]
    [EnableRateLimiting(RateLimiting.AccountVerifyPolicy)]
    [ProducesResponseType<CredentialsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Verify(VerifyRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.VerifyAsync(request.Token, cancellationToken);

        switch (result.Status)
        {
            case AccountStatus.Ok:
                // The response holds a secret; no cache or proxy may keep it.
                Response.Headers.CacheControl = "no-store";
                var credentials = result.Credentials!;
                return Ok(new CredentialsResponse(credentials.UserId, credentials.Email, credentials.ApiKey));

            case AccountStatus.AlreadyRegistered:
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Already registered",
                    detail: "This address already has an account. Use the recovery link on the sign-in page if you lost the key.");

            default:
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["token"] = ["The link is invalid, expired or already used."] }));
        }
    }

    private IActionResult MailRequestResponse(AccountResult result)
    {
        switch (result.Status)
        {
            case AccountStatus.Ok:
                return Accepted();

            case AccountStatus.Invalid:
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]> { [result.Field!] = [result.Error!] }));

            default:
                Response.Headers.RetryAfter = "30";
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Could not send the email",
                    detail: "The email could not be sent. Please try again shortly.");
        }
    }
}
