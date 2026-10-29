namespace UrlShortener.Core.Storage;

/// <summary>Lets an action happen at most once per time window for a given key (e.g. one mail per address per minute).</summary>
public interface ICooldownStore
{
    /// <summary>
    /// Starts the cooldown and returns true if none was running for <paramref name="key"/>;
    /// returns false, changing nothing, while an earlier one is still running.
    /// </summary>
    Task<bool> TryStartAsync(string key, TimeSpan duration, CancellationToken cancellationToken = default);
}
