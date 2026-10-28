using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Shortener.Api.Authentication;
using UrlShortener.Shortener.Api.Models;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Controllers;

/// <summary>A user's own short links.</summary>
[ApiController]
[Authorize]
[Route("api/v1/users")]
public sealed class UsersController(ILinkQueryService queries) : ControllerBase
{
    /// <summary>Lists your short links, newest first.</summary>
    /// <param name="userId">Your user id; you cannot list another user's links.</param>
    /// <param name="pageSize">How many links to return (1-100, default 50).</param>
    /// <param name="pageToken">The next_page_token of the previous page, to continue where it ended.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">One page of links.</response>
    /// <response code="400">The page size or page token is invalid.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="403">The user id is not yours.</response>
    [HttpGet("{userId}/urls", Name = "ListUserUrls")]
    [ProducesResponseType<UserUrlsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UserUrlsResponse>> List(
        string userId,
        [FromQuery(Name = "page_size"), Range(1, 100)] int pageSize = 50,
        [FromQuery(Name = "page_token"), MaxLength(512)] string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        var result = await queries.ListUserUrlsAsync(User.GetUserId(), userId, pageSize, pageToken, cancellationToken);

        switch (result.Status)
        {
            case QueryStatus.Ok:
                var page = result.Value!;
                var items = page.Items.Select(url => new UserUrlItem(url.ShortCode, url.LongUrl, url.CreatedAt, url.ExpiresAt)).ToList();
                return new UserUrlsResponse(items, page.NextPageToken);

            case QueryStatus.InvalidPageToken:
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]> { ["page_token"] = ["The page token is invalid."] }));

            default:
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden",
                    detail: "You can only list your own links.");
        }
    }
}
