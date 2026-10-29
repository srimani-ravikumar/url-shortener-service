using Cassandra;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure.Cassandra;

/// <summary>Cassandra-backed email verification tokens (table email_tokens, rows expire by TTL).</summary>
public sealed class EmailTokenStore(CassandraConnection connection) : IEmailTokenStore
{
    public async Task SaveAsync(string tokenHash, EmailToken token, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var insert = context.Statements.InsertEmailToken.Bind(
            tokenHash,
            token.Purpose,
            token.Email,
            token.CreatedAt,
            (int)lifetime.TotalSeconds);
        await context.ExecuteAsync(insert, cancellationToken);
    }

    public async Task<EmailToken?> TakeAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var context = await connection.GetContextAsync(cancellationToken);

        var result = await context.ExecuteAsync(context.Statements.SelectEmailToken.Bind(tokenHash), cancellationToken);
        var row = result.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        // Several callers may have read the row; the conditional delete lets exactly one of them win.
        var delete = await context.ExecuteAsync(context.Statements.DeleteEmailToken.Bind(tokenHash), cancellationToken);
        if (!delete.First().GetValue<bool>("[applied]"))
        {
            return null;
        }

        return new EmailToken(
            row.GetValue<string>("purpose"),
            row.GetValue<string>("email"),
            row.GetValue<DateTimeOffset>("created_at"));
    }
}
