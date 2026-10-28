using UrlShortener.Infrastructure;

namespace UrlShortener.Shortener.Api.Services;

/// <summary>
/// Runs once at startup, before the service accepts requests: if Redis has lost the ID counter, it is moved
/// past the last checkpoint so old numbers are not handed out again. If Redis or Cassandra are still
/// starting up it waits and retries, and gives up (so the container restarts) after about a minute.
/// </summary>
internal sealed class CounterRecoveryService(IdCounterDurability durability, ILogger<CounterRecoveryService> logger) : IHostedService
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await durability.RestoreIfLostAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (TransientFailure.IsTransient(ex) && attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Storage not ready for ID counter recovery (attempt {Attempt}/{Max}); retrying.", attempt, MaxAttempts);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
