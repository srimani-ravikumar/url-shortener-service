namespace UrlShortener.Redirector.Api.Services;

/// <summary>
/// Runs work that the response must not wait for (writing the cache, counting a click).
/// A failure is logged and otherwise ignored: neither may ever break or delay a redirect.
/// </summary>
internal static class BackgroundTask
{
    /// <summary>Lets <paramref name="task"/> finish on its own and logs it if it fails.</summary>
    public static void Observe(Task task, ILogger logger, string description)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            finished => logger.LogWarning(finished.Exception, "Background work failed: {Description}.", description),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
