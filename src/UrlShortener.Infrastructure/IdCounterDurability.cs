using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Storage;

namespace UrlShortener.Infrastructure;

/// <summary>Settings for protecting the ID counter against data loss (configuration section "IdCounter").</summary>
public sealed class IdCounterOptions
{
    public const string SectionName = "IdCounter";

    /// <summary>Name under which the checkpoint is stored.</summary>
    public string Name { get; set; } = "short_code";

    /// <summary>
    /// How far past the checkpoint to restart after a loss. It must exceed the number of ids that can be
    /// handed out between two checkpoints (checkpoint interval x peak creates per second).
    /// </summary>
    public long SafetyGap { get; set; } = 100_000;
}

/// <summary>
/// Keeps the Redis ID counter from handing out old numbers again. Redis could lose the counter (a crash,
/// a flush); the last checkpoint saved in Cassandra tells us roughly where it was.
/// </summary>
public sealed class IdCounterDurability(
    IIdCounter counter,
    IIdCounterCheckpointStore checkpoints,
    IOptions<IdCounterOptions> options,
    ILogger<IdCounterDurability> logger)
{
    private readonly IdCounterOptions _options = options.Value;

    /// <summary>
    /// Call at startup, before handing out ids. Returns true if the counter had been lost and was moved forward.
    /// </summary>
    public async Task<bool> RestoreIfLostAsync(CancellationToken cancellationToken = default)
    {
        var checkpoint = await checkpoints.GetAsync(_options.Name, cancellationToken);
        if (checkpoint is null)
        {
            // Nothing was ever saved, so there is nothing to protect yet (a brand new system).
            return false;
        }

        var reseeded = await counter.ReseedIfBehindAsync(checkpoint.Value, _options.SafetyGap, cancellationToken);
        if (reseeded)
        {
            logger.LogWarning(
                "ID counter was behind its checkpoint {Checkpoint}; moved it forward by {Gap} to avoid reusing ids.",
                checkpoint.Value,
                _options.SafetyGap);
        }

        return reseeded;
    }

    /// <summary>
    /// Saves the counter's current value as the new checkpoint. Run it periodically.
    /// It never moves the checkpoint backwards, so a lost (or freshly restarted) counter cannot overwrite it.
    /// </summary>
    public async Task SaveCheckpointAsync(CancellationToken cancellationToken = default)
    {
        var current = await counter.GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return;
        }

        var saved = await checkpoints.GetAsync(_options.Name, cancellationToken);
        if (saved is null || current.Value > saved.Value)
        {
            await checkpoints.SaveAsync(_options.Name, current.Value, cancellationToken);
        }
    }
}
