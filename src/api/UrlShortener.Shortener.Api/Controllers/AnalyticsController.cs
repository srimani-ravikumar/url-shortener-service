using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Shortener.Api.Authentication;
using UrlShortener.Shortener.Api.Models;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Controllers;

/// <summary>Click statistics for short links.</summary>
[ApiController]
[Authorize]
[Route("api/v1/analytics")]
public sealed class AnalyticsController(ILinkQueryService queries) : ControllerBase
{
    /// <summary>Gets click statistics for one of your short links.</summary>
    /// <param name="shortCode">The link's short code.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <response code="200">The statistics. Counts are updated every few seconds, so they can lag slightly.</response>
    /// <response code="401">The API key is missing or invalid.</response>
    /// <response code="403">The link belongs to another user.</response>
    /// <response code="404">There is no such link.</response>
    [HttpGet("{shortCode}", Name = "GetAnalytics")]
    [ProducesResponseType<AnalyticsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AnalyticsResponse>> Get(string shortCode, CancellationToken cancellationToken)
    {
        var result = await queries.GetAnalyticsAsync(User.GetUserId(), shortCode, cancellationToken);

        return result.Status switch
        {
            QueryStatus.Ok => new AnalyticsResponse(
                result.Value!.ShortCode,
                result.Value.Clicks.TotalClicks,
                result.Value.CreatedAt,
                result.Value.Clicks.LastAccessed),
            QueryStatus.Forbidden => Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "This link belongs to another user."),
            _ => Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Link not found",
                detail: "There is no link with that short code."),
        };
    }
}
