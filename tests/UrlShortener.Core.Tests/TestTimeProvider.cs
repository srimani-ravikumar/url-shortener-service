namespace UrlShortener.Core.Tests;

/// <summary>A clock whose time is set by the test, so expiry logic can be tested without waiting.</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    public TestTimeProvider(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}
