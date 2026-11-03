using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Shortener.Api.Authentication;
using UrlShortener.Shortener.Api.Configuration;
using UrlShortener.Shortener.Api.Models;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Controllers;

/// <summary>Creates short links.</summary>
[ApiController]
[Authorize]
[Route("api/v1/shorten")]
public sealed class ShortenController(IShortenService shortenService) : ControllerBase
{
    /// <summary>Creates a short link for a long URL.</summary>
    /// <remarks>
    /// Send an optional <c>custom_alias</c> to choose the short code yourself; otherwise one is generated.
    /// The owner of the link is the user the API key belongs to.
    /// </remarks>
    /// <response code="201">The link was created; the Location header holds the short URL.</response>
    /// <response code="400">The URL, alias or expiry is invalid.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="409">The custom alias is already taken.</response>
    /// <response code="429">Too many links created too quickly.</response>
    /// <response code="503">Storage is temporarily unavailable; retry shortly.</response>
    [HttpPost(Name = "CreateShortUrl")]
    [EnableRateLimiting(RateLimiting.ShortenPolicy)]
    [ProducesResponseType<ShortUrlResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ShortUrlResponse>> Create(
        CreateShortUrlRequest request,
        CancellationToken cancellationToken)
    {
        var result = await shortenService.CreateAsync(
            User.GetUserId(),
            request.LongUrl,
            request.CustomAlias,
            request.ExpiresAt,
            cancellationToken);

        switch (result.Status)
        {
            case ShortenStatus.Created:
                var link = result.Link!;
                return Created(result.ShortUrl!, new ShortUrlResponse(result.ShortUrl!, link.LongUrl, link.ExpiresAt, link.CreatedAt));

            case ShortenStatus.Invalid:
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]> { [result.Field!] = [result.Error!] }));

            case ShortenStatus.AliasTaken:
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Alias already taken",
                    detail: "That custom alias is already in use. Choose another one.");

            default:
                // Not the caller's fault and almost always gone on retry.
                Response.Headers.RetryAfter = "1";
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Could not generate a short code",
                    detail: "Please try again.");
        }
    }
}
