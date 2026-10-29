using Cassandra;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed click statistics (tables url_clicks and url_last_access).</summary>
public sealed class ClickRepository(CassandraConnection connection) : IClickRepository
{
    public async Task AddClicksAsync(
        string shortCode,
        long count,
        DateTimeOffset lastAccessed,
        CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        // The timestamp is idempotent but the counter increment is not. Writing the timestamp first means
        // a failure between the two writes can be retried without counting the clicks twice.
        await context.ExecuteAsync(context.Statements.UpsertLastAccess.Bind(lastAccessed, shortCode), cancellationToken);
        await context.ExecuteAsync(context.Statements.AddClicks.Bind(count, shortCode), cancellationToken);
    }

    public async Task<ClickStats> GetAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var clicks = context.ExecuteAsync(context.Statements.SelectClicks.Bind(shortCode), cancellationToken);
        var lastAccess = context.ExecuteAsync(context.Statements.SelectLastAccess.Bind(shortCode), cancellationToken);
        await Task.WhenAll(clicks, lastAccess);

        var clickRow = clicks.Result.FirstOrDefault();
        var lastAccessRow = lastAccess.Result.FirstOrDefault();

        return new ClickStats(
            clickRow?.GetValue<long>("click_count") ?? 0,
            lastAccessRow?.GetValue<DateTimeOffset?>("last_accessed"));
    }

    public async Task DeleteAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        await context.ExecuteAsync(context.Statements.DeleteClicks.Bind(shortCode), cancellationToken);
        await context.ExecuteAsync(context.Statements.DeleteLastAccess.Bind(shortCode), cancellationToken);
    }
}
