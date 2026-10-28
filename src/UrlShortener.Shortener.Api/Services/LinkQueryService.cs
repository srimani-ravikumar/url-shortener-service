using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Services;

/// <inheritdoc />
internal sealed class LinkQueryService(IUrlMappingRepository links, IClickRepository clicks) : ILinkQueryService
{
    public async Task<QueryResult<LinkAnalytics>> GetAnalyticsAsync(
        string callerUserId,
        string shortCode,
        CancellationToken cancellationToken)
    {
        var link = await links.GetAsync(shortCode, cancellationToken);
        if (link is null)
        {
            return QueryResult<LinkAnalytics>.NotFound;
        }

        if (link.UserId != callerUserId)
        {
            return QueryResult<LinkAnalytics>.Forbidden;
        }

        var stats = await clicks.GetAsync(shortCode, cancellationToken);
        return QueryResult<LinkAnalytics>.Ok(new LinkAnalytics(shortCode, stats, link.CreatedAt));
    }

    public async Task<QueryResult<PagedResult<UserUrl>>> ListUserUrlsAsync(
        string callerUserId,
        string userId,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken)
    {
        if (userId != callerUserId)
        {
            return QueryResult<PagedResult<UserUrl>>.Forbidden;
        }

        try
        {
            var page = await links.ListByUserAsync(userId, pageSize, pageToken, cancellationToken);
            return QueryResult<PagedResult<UserUrl>>.Ok(page);
        }
        catch (InvalidPageTokenException)
        {
            return QueryResult<PagedResult<UserUrl>>.InvalidPageToken;
        }
    }
}
