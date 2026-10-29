using Cassandra;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using UrlShortener.Infrastructure.Cassandra;

namespace UrlShortener.Infrastructure.Tests;

/// <summary>
/// Base class for tests that talk to the real Cassandra and Redis from deploy/docker-compose.yml.
/// Every fixture uses its own Redis key prefix and its own short codes, so tests never touch real data
/// and clean up after themselves.
/// </summary>
/// <remarks>
/// Start the stack first: docker compose -f deploy/docker-compose.yml up -d.
/// If it is not running the tests are skipped, unless REQUIRE_INTEGRATION_STACK=true (used in CI), which fails them.
/// </remarks>
[Category("Integration")]
public abstract class IntegrationTestBase
{
    private readonly List<string> _shortCodes = [];
    private readonly List<string> _userIds = [];
    private readonly List<string> _apiKeyHashes = [];
    private readonly List<string> _emails = [];
    private readonly List<string> _tokenHashes = [];

    protected ServiceProvider Services { get; private set; } = null!;

    /// <summary>Prefix of every Redis key this fixture creates.</summary>
    protected string KeyPrefix { get; } = $"it-{Guid.NewGuid():N}:";

    /// <summary>Name of the ID counter checkpoint this fixture uses.</summary>
    protected string CheckpointName { get; } = $"it-{Guid.NewGuid():N}";

    protected IDatabase Redis => Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    [OneTimeSetUp]
    public async Task ConnectToTheStack()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cassandra:ContactPoints"] = Environment.GetEnvironmentVariable("INTEGRATION_CASSANDRA_HOST") ?? "localhost",
                ["Redis:ConnectionString"] = Environment.GetEnvironmentVariable("INTEGRATION_REDIS") ?? "localhost:6379",
                ["Redis:KeyPrefix"] = KeyPrefix,
                ["IdCounter:Name"] = CheckpointName,
            })
            .Build();

        Services = new ServiceCollection()
            .AddLogging()
            .AddUrlShortenerInfrastructure(configuration)
            .BuildServiceProvider();

        try
        {
            await Services.GetRequiredService<CassandraConnection>().GetContextAsync();
            await Redis.PingAsync();
        }
        catch (Exception ex)
        {
            await Services.DisposeAsync();

            if (Environment.GetEnvironmentVariable("REQUIRE_INTEGRATION_STACK") == "true")
            {
                throw;
            }

            Assert.Ignore($"Cassandra/Redis are not reachable ({ex.GetType().Name}). Start them with: docker compose -f deploy/docker-compose.yml up -d");
        }
    }

    [OneTimeTearDown]
    public async Task CleanUp()
    {
        if (Services is null)
        {
            return;
        }

        await ClearRedisKeysAsync();

        foreach (var shortCode in _shortCodes)
        {
            await ExecuteCqlAsync("DELETE FROM url_mappings WHERE short_code = ?", shortCode);
            await ExecuteCqlAsync("DELETE FROM url_clicks WHERE short_code = ?", shortCode);
            await ExecuteCqlAsync("DELETE FROM url_last_access WHERE short_code = ?", shortCode);
        }

        foreach (var userId in _userIds)
        {
            await ExecuteCqlAsync("DELETE FROM urls_by_user WHERE user_id = ?", userId);
            await ExecuteCqlAsync("DELETE FROM users WHERE user_id = ?", userId);
        }

        foreach (var hash in _apiKeyHashes)
        {
            await ExecuteCqlAsync("DELETE FROM users_by_api_key WHERE api_key_hash = ?", hash);
        }

        foreach (var email in _emails)
        {
            await ExecuteCqlAsync("DELETE FROM users_by_email WHERE email = ?", email);
        }

        foreach (var tokenHash in _tokenHashes)
        {
            await ExecuteCqlAsync("DELETE FROM email_tokens WHERE token_hash = ?", tokenHash);
        }

        await ExecuteCqlAsync("DELETE FROM id_counter_checkpoint WHERE name = ?", CheckpointName);
        await Services.DisposeAsync();
    }

    /// <summary>A short code that is unique to this test run; it is deleted when the fixture finishes.</summary>
    protected string NewShortCode()
    {
        var code = "it-" + Guid.NewGuid().ToString("N")[..12];
        _shortCodes.Add(code);
        return code;
    }

    /// <summary>A user id that is unique to this test run; its link list is deleted when the fixture finishes.</summary>
    protected string NewUserId()
    {
        var userId = "it-user-" + Guid.NewGuid().ToString("N")[..12];
        _userIds.Add(userId);
        return userId;
    }

    protected string NewApiKeyHash()
    {
        var hash = "it-hash-" + Guid.NewGuid().ToString("N");
        _apiKeyHashes.Add(hash);
        return hash;
    }

    /// <summary>An email address that is unique to this test run; its claim row is deleted when the fixture finishes.</summary>
    protected string NewEmail()
    {
        var email = $"it-{Guid.NewGuid():N}@example.test";
        _emails.Add(email);
        return email;
    }

    /// <summary>A token hash that is unique to this test run; its row is deleted when the fixture finishes.</summary>
    protected string NewTokenHash()
    {
        var hash = "it-token-" + Guid.NewGuid().ToString("N");
        _tokenHashes.Add(hash);
        return hash;
    }

    /// <summary>Runs raw CQL, for setting up or inspecting data behind the repositories' backs.</summary>
    protected async Task<RowSet> ExecuteCqlAsync(string cql, params object[] values)
    {
        var context = await Services.GetRequiredService<CassandraConnection>().GetContextAsync();
        return await context.Session.ExecuteAsync(new SimpleStatement(cql, values));
    }

    /// <summary>Deletes every Redis key created through this fixture's prefix.</summary>
    protected async Task ClearRedisKeysAsync()
    {
        var multiplexer = Services.GetRequiredService<IConnectionMultiplexer>();
        foreach (var endpoint in multiplexer.GetEndPoints())
        {
            var server = multiplexer.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*"))
            {
                await Redis.KeyDeleteAsync(key);
            }
        }
    }
}
