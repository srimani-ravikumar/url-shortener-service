namespace UrlShortener.Core.Storage;

/// <summary>The global counter that gives every new short link a unique number.</summary>
public interface IIdCounter
{
    /// <summary>Returns the next number; never returns the same number twice (while the counter is intact).</summary>
    Task<long> NextAsync(CancellationToken cancellationToken = default);

    /// <summary>The last number handed out, or null if the counter does not exist yet.</summary>
    Task<long?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// If the counter is missing or below <paramref name="checkpoint"/> (Redis lost data), moves it to
    /// <paramref name="checkpoint"/> + <paramref name="safetyGap"/>. Returns true if it was moved.
    /// </summary>
    Task<bool> ReseedIfBehindAsync(long checkpoint, long safetyGap, CancellationToken cancellationToken = default);
}

/// <summary>Durable copy of the counter's progress, used to recover when the counter is lost.</summary>
public interface IIdCounterCheckpointStore
{
    Task<long?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task SaveAsync(string name, long value, CancellationToken cancellationToken = default);
}
