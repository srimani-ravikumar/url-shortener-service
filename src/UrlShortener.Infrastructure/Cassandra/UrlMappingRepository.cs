using System.Buffers.Text;
using Cassandra;
using Microsoft.Extensions.Logging;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed storage of short links (tables url_mappings and urls_by_user).</summary>
public sealed class UrlMappingRepository(CassandraConnection connection, ILogger<UrlMappingRepository> logger)
    : IUrlMappingRepository
{
    public async Task<bool> TryInsertAsync(UrlMapping mapping, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        // IF NOT EXISTS makes Cassandra check-and-write in one atomic step, so two requests
        // claiming the same short code can never both succeed.
        var insert = context.Statements.InsertMapping.Bind(
            mapping.ShortCode,
            mapping.LongUrl,
            mapping.UserId,
            mapping.CreatedAt,
            mapping.ExpiresAt,
            mapping.IsCustom,
            ttlSeconds);
        var result = await context.ExecuteAsync(insert, cancellationToken);

        // A conditional write answers with a single row whose "[applied]" column says whether it was written.
        if (!result.First().GetValue<bool>("[applied]"))
        {
            return false;
        }

        await AddToUserListAsync(context, mapping, ttlSeconds, cancellationToken);
        return true;
    }

    public async Task<UrlMapping?> GetAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectMapping.Bind(shortCode), cancellationToken);
        var row = result.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return new UrlMapping(
            shortCode,
            row.GetValue<string>("long_url"),
            row.GetValue<string>("user_id"),
            row.GetValue<DateTimeOffset>("created_at"),
            row.GetValue<DateTimeOffset>("expires_at"),
            row.GetValue<bool>("is_custom"));
    }

    public async Task<PagedResult<UserUrl>> ListByUserAsync(
        string userId,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var context = await connection.GetContextAsync(cancellationToken);

        // AutoPage(false) makes the driver return just one page instead of silently fetching them all.
        var query = context.Statements.SelectUserUrls.Bind(userId).SetPageSize(pageSize).SetAutoPage(false);
        if (pageToken is not null)
        {
            query.SetPagingState(DecodePageToken(pageToken));
        }

        RowSet page;
        try
        {
            page = await context.ExecuteAsync(query, cancellationToken);
        }
        catch (Exception ex) when (pageToken is not null && ex is InvalidQueryException or ProtocolErrorException)
        {
            // Cassandra rejects a paging state it did not produce.
            throw new InvalidPageTokenException(ex);
        }

        var items = page
            .Select(row => new UserUrl(
                row.GetValue<string>("short_code"),
                row.GetValue<string>("long_url"),
                row.GetValue<DateTimeOffset>("created_at"),
                row.GetValue<DateTimeOffset>("expires_at")))
            .ToList();

        // Cassandra's paging state is null on the last page.
        var nextToken = page.PagingState is null ? null : Base64Url.EncodeToString(page.PagingState);
        return new PagedResult<UserUrl>(items, nextToken);
    }

    // The link itself is already stored and works. If only this second write fails, the link is just
    // missing from the owner's list, so log it instead of failing a request that did succeed.
    private async Task AddToUserListAsync(
        CassandraContext context,
        UrlMapping mapping,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var insert = context.Statements.InsertUserUrl.Bind(
                mapping.UserId,
                mapping.CreatedAt,
                mapping.ShortCode,
                mapping.LongUrl,
                mapping.ExpiresAt,
                ttlSeconds);
            await context.ExecuteAsync(insert, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Link {ShortCode} was created but could not be added to the list of user {UserId}.", mapping.ShortCode, mapping.UserId);
        }
    }

    private static byte[] DecodePageToken(string token)
    {
        try
        {
            return Base64Url.DecodeFromChars(token);
        }
        catch (FormatException ex)
        {
            throw new InvalidPageTokenException(ex);
        }
    }
}
