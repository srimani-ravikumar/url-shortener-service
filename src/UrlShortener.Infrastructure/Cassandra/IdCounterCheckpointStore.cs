using Cassandra;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed store for the ID counter checkpoint (table id_counter_checkpoint).</summary>
public sealed class IdCounterCheckpointStore(CassandraConnection connection) : IIdCounterCheckpointStore
{
    public async Task<long?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectCheckpoint.Bind(name), cancellationToken);
        return result.FirstOrDefault()?.GetValue<long>("value");
    }

    public async Task SaveAsync(string name, long value, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        await context.ExecuteAsync(context.Statements.UpsertCheckpoint.Bind(name, value), cancellationToken);
    }
}
