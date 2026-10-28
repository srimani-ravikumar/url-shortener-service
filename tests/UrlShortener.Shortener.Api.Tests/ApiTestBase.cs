namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Gives every test its own in-memory API with empty storage.</summary>
public abstract class ApiTestBase
{
    internal ShortenerApiFactory Factory { get; private set; } = null!;

    [SetUp]
    public void CreateFactory() => Factory = new ShortenerApiFactory();

    [TearDown]
    public async Task DisposeFactory() => await Factory.DisposeAsync();
}
