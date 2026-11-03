using System.Diagnostics;
using Cassandra;
using Microsoft.Extensions.Options;
using UrlShortener.Infrastructure.Telemetry;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>An open Cassandra session together with the prepared statements that go with it.</summary>
public sealed class CassandraContext(ISession session, CassandraStatements statements)
{
    public ISession Session { get; } = session;

    public CassandraStatements Statements { get; } = statements;

    /// <summary>Runs a statement. The driver cannot cancel a query, so cancelling only stops the waiting.</summary>
    public async Task<RowSet> ExecuteAsync(IStatement statement, CancellationToken cancellationToken)
    {
        using var span = UrlShortenerTelemetry.StartClientSpan("cassandra.execute", "cassandra");

        // A prepared statement's text has only '?' placeholders, never values, so it is safe to record.
        if (span is not null && statement is BoundStatement bound)
        {
            span.SetTag("db.statement", bound.PreparedStatement.QueryString);
        }

        try
        {
            return await Session.ExecuteAsync(statement).WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            span?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }
}

/// <summary>
/// Owns the Cassandra connection. It connects on first use rather than at startup, so a service can
/// start while Cassandra is still booting; a failed attempt is not remembered, so the next call retries.
/// </summary>
public sealed class CassandraConnection(IOptions<CassandraOptions> options) : IAsyncDisposable
{
    private readonly CassandraOptions _options = options.Value;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ICluster? _cluster;
    private CassandraContext? _context;

    public async Task<CassandraContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        if (_context is not null)
        {
            return _context;
        }

        // Only one caller connects; the others wait and then reuse the result.
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            return _context ??= await ConnectAsync();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.ShutdownAsync();
        }

        _connectLock.Dispose();
    }

    private async Task<CassandraContext> ConnectAsync()
    {
        var contactPoints = _options.ContactPoints.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var cluster = Cluster.Builder()
            .AddContactPoints(contactPoints)
            .WithPort(_options.Port)
            .WithSocketOptions(new SocketOptions()
                .SetConnectTimeoutMillis(_options.ConnectTimeoutMs)
                .SetReadTimeoutMillis(_options.QueryTimeoutMs))
            .WithQueryOptions(new QueryOptions()
                // Reads and writes need a majority of the local datacenter's replicas...
                .SetConsistencyLevel(ConsistencyLevel.LocalQuorum)
                // ...and conditional (IF NOT EXISTS) writes use a cluster-wide consensus round.
                .SetSerialConsistencyLevel(ConsistencyLevel.Serial))
            .Build();

        try
        {
            var session = await cluster.ConnectAsync(_options.Keyspace);
            var context = new CassandraContext(session, await CassandraStatements.PrepareAsync(session));
            _cluster = cluster;
            return context;
        }
        catch
        {
            await cluster.ShutdownAsync();
            throw;
        }
    }
}
