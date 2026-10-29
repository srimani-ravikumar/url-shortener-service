using System.Collections.Concurrent;
using UrlShortener.Core.Security;
using UrlShortener.Core.Storage;

namespace UrlShortener.TestSupport;

/// <summary>A clock whose time is set by the test, so expiry can be tested without waiting.</summary>
public sealed class TestTimeProvider : TimeProvider
{
    public TestTimeProvider(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>In-memory link storage that behaves like Cassandra's conditional insert and paging.</summary>
public sealed class FakeUrlMappingRepository : IUrlMappingRepository
{
    private int _getCalls;

    public ConcurrentDictionary<string, (UrlMapping Link, int TtlSeconds)> Stored { get; } = new();

    // Kept apart from Stored, like Cassandra's urls_by_user table, so deleting a link does not shift a page in progress.
    private readonly ConcurrentDictionary<string, UrlMapping> _userList = new();

    /// <summary>How many times <see cref="GetAsync"/> was called, to tell cache hits from database reads.</summary>
    public int GetCalls => _getCalls;

    /// <summary>When set, every call throws it, to simulate an outage or a bug.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Adds a link directly, bypassing the creation rules.</summary>
    public void Add(UrlMapping link)
    {
        Stored[link.ShortCode] = (link, 100);
        _userList[link.ShortCode] = link;
    }

    public Task<bool> TryInsertAsync(UrlMapping mapping, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        var added = Stored.TryAdd(mapping.ShortCode, (mapping, ttlSeconds));
        if (added)
        {
            _userList[mapping.ShortCode] = mapping;
        }

        return Task.FromResult(added);
    }

    public Task DeleteAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Stored.TryRemove(shortCode, out _);
        return Task.CompletedTask;
    }

    public Task DeleteUserListAsync(string userId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        foreach (var entry in _userList.Where(entry => entry.Value.UserId == userId).ToList())
        {
            _userList.TryRemove(entry.Key, out _);
        }

        return Task.CompletedTask;
    }

    public Task<UrlMapping?> GetAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getCalls);
        ThrowIfFailing();
        return Task.FromResult(Stored.TryGetValue(shortCode, out var entry) ? entry.Link : null);
    }

    public Task<PagedResult<UserUrl>> ListByUserAsync(
        string userId,
        int pageSize,
        string? pageToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();

        // The token is just the offset; anything that is not a number was not issued by this fake.
        var offset = 0;
        if (pageToken is not null && !int.TryParse(pageToken, out offset))
        {
            throw new InvalidPageTokenException();
        }

        var all = _userList.Values
            .Where(link => link.UserId == userId)
            .OrderByDescending(link => link.CreatedAt)
            .ThenBy(link => link.ShortCode)
            .ToList();

        var items = all
            .Skip(offset)
            .Take(pageSize)
            .Select(link => new UserUrl(link.ShortCode, link.LongUrl, link.CreatedAt, link.ExpiresAt))
            .ToList();
        var nextToken = offset + pageSize < all.Count ? (offset + pageSize).ToString() : null;

        return Task.FromResult(new PagedResult<UserUrl>(items, nextToken));
    }

    private void ThrowIfFailing()
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
    }
}

/// <summary>In-memory users, each with an API key that the test can use.</summary>
public sealed class FakeUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<string, string> _userIdsByKeyHash = new();
    private readonly ConcurrentDictionary<string, UserAccount> _accounts = new();
    private readonly object _writeLock = new();

    /// <summary>When set, every call throws it, to simulate an outage.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Every account currently stored.</summary>
    public IReadOnlyCollection<UserAccount> Accounts => _accounts.Values.ToList();

    /// <summary>How many API keys currently authenticate someone.</summary>
    public int KeyCount => _userIdsByKeyHash.Count;

    /// <summary>
    /// Creates an account with an API key and returns the key (only its hash is stored, as in production).
    /// The email defaults to an address derived from the user id.
    /// </summary>
    public string AddUser(string userId, string? email = null)
    {
        var apiKey = "test-key-" + Guid.NewGuid().ToString("N");
        var hash = ApiKeyHasher.Hash(apiKey);
        _userIdsByKeyHash[hash] = userId;
        _accounts[userId] = new UserAccount(userId, email ?? $"{userId}@example.test", DateTimeOffset.UnixEpoch, hash);
        return apiKey;
    }

    public Task<string?> GetUserIdByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(_userIdsByKeyHash.TryGetValue(apiKeyHash, out var userId) ? userId : null);
    }

    public Task<UserAccount?> GetByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(_accounts.TryGetValue(userId, out var account) ? account : null);
    }

    public Task<UserAccount?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(_accounts.Values.FirstOrDefault(account => account.Email == email));
    }

    public Task<bool> TryCreateAsync(UserAccount user, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();

        // One lock stands in for Cassandra's conditional insert on the email.
        lock (_writeLock)
        {
            if (_accounts.Values.Any(account => account.Email == user.Email))
            {
                return Task.FromResult(false);
            }

            _accounts[user.UserId] = user;
            _userIdsByKeyHash[user.ApiKeyHash!] = user.UserId;
            return Task.FromResult(true);
        }
    }

    public Task ReplaceApiKeyAsync(string userId, string newApiKeyHash, string? presentedApiKeyHash, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();

        lock (_writeLock)
        {
            var account = _accounts[userId];
            _userIdsByKeyHash[newApiKeyHash] = userId;
            _accounts[userId] = account with { ApiKeyHash = newApiKeyHash };

            foreach (var oldHash in new[] { account.ApiKeyHash, presentedApiKeyHash })
            {
                if (oldHash is not null && oldHash != newApiKeyHash)
                {
                    _userIdsByKeyHash.TryRemove(oldHash, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(UserAccount user, string? presentedApiKeyHash, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();

        lock (_writeLock)
        {
            foreach (var hash in new[] { user.ApiKeyHash, presentedApiKeyHash })
            {
                if (hash is not null)
                {
                    _userIdsByKeyHash.TryRemove(hash, out _);
                }
            }

            _accounts.TryRemove(user.UserId, out _);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
    }
}

/// <summary>In-memory email tokens; a test can read the "emailed" token it needs, and expire it.</summary>
public sealed class FakeEmailTokenStore : IEmailTokenStore
{
    private readonly ConcurrentDictionary<string, EmailToken> _tokens = new();

    /// <summary>How long the most recently saved token was told to live.</summary>
    public TimeSpan LastLifetime { get; private set; }

    /// <summary>How many tokens are waiting to be used.</summary>
    public int Count => _tokens.Count;

    public Task SaveAsync(string tokenHash, EmailToken token, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        _tokens[tokenHash] = token;
        LastLifetime = lifetime;
        return Task.CompletedTask;
    }

    public Task<EmailToken?> TakeAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.TryRemove(tokenHash, out var token) ? token : null);

    /// <summary>Simulates the token expiring.</summary>
    public void Clear() => _tokens.Clear();
}

/// <summary>In-memory cooldowns; time only moves when the test says so.</summary>
public sealed class FakeCooldownStore : ICooldownStore
{
    private readonly ConcurrentDictionary<string, TimeSpan> _running = new();

    /// <summary>The duration of the cooldown most recently started.</summary>
    public TimeSpan LastDuration { get; private set; }

    public Task<bool> TryStartAsync(string key, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        LastDuration = duration;
        return Task.FromResult(_running.TryAdd(key, duration));
    }

    /// <summary>Simulates every cooldown running out.</summary>
    public void ExpireAll() => _running.Clear();
}

/// <summary>In-memory click statistics.</summary>
public sealed class FakeClickRepository : IClickRepository
{
    private int _addCalls;

    public ConcurrentDictionary<string, ClickStats> Stats { get; } = new();

    /// <summary>How many times <see cref="AddClicksAsync"/> was called, successful or not.</summary>
    public int AddCalls => _addCalls;

    /// <summary>When set, <see cref="AddClicksAsync"/> throws it, like a Cassandra outage.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Runs at the start of every <see cref="AddClicksAsync"/> call, to simulate things happening mid-flush.</summary>
    public Action<string>? OnAddClicks { get; set; }

    public Task AddClicksAsync(string shortCode, long count, DateTimeOffset lastAccessed, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _addCalls);
        OnAddClicks?.Invoke(shortCode);

        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        Stats.AddOrUpdate(
            shortCode,
            new ClickStats(count, lastAccessed),
            (_, existing) => new ClickStats(existing.TotalClicks + count, lastAccessed));
        return Task.CompletedTask;
    }

    public Task<ClickStats> GetAsync(string shortCode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stats.TryGetValue(shortCode, out var stats) ? stats : new ClickStats(0, null));

    public Task DeleteAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        Stats.TryRemove(shortCode, out _);
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory ID counter with the same reseed rule as the Redis one.</summary>
public sealed class FakeIdCounter : IIdCounter
{
    private long _value;
    private bool _exists;

    public void Set(long value)
    {
        _value = value;
        _exists = true;
    }

    public Task<long> NextAsync(CancellationToken cancellationToken = default)
    {
        _exists = true;
        return Task.FromResult(Interlocked.Increment(ref _value));
    }

    public Task<long?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_exists ? _value : (long?)null);

    public Task<bool> ReseedIfBehindAsync(long checkpoint, long safetyGap, CancellationToken cancellationToken = default)
    {
        if (_value >= checkpoint)
        {
            return Task.FromResult(false);
        }

        Set(checkpoint + safetyGap);
        return Task.FromResult(true);
    }
}

/// <summary>In-memory counter checkpoints.</summary>
public sealed class FakeCheckpointStore : IIdCounterCheckpointStore
{
    private readonly ConcurrentDictionary<string, long> _values = new();

    public Task<long?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(name, out var value) ? value : (long?)null);

    public Task SaveAsync(string name, long value, CancellationToken cancellationToken = default)
    {
        _values[name] = value;
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory URL cache that remembers the TTL it was given, so tests can check it.</summary>
public sealed class FakeUrlCache : IUrlCache
{
    private int _getCalls;

    public ConcurrentDictionary<string, (CachedUrl Url, TimeSpan Ttl)> Entries { get; } = new();

    /// <summary>How many times <see cref="GetAsync"/> was called.</summary>
    public int GetCalls => _getCalls;

    /// <summary>When true the cache behaves like an unreachable Redis: reads miss and writes are dropped.</summary>
    public bool Unavailable { get; set; }

    public Task<CachedUrl?> GetAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getCalls);
        return Task.FromResult(!Unavailable && Entries.TryGetValue(shortCode, out var entry) ? entry.Url : null);
    }

    public Task SetAsync(string shortCode, CachedUrl url, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (!Unavailable && ttl > TimeSpan.Zero)
        {
            Entries[shortCode] = (url, ttl);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        // Like the Redis cache, removal is not best-effort: an unreachable cache is an error.
        if (Unavailable)
        {
            return Task.FromException(new TimeoutException("The cache is unavailable."));
        }

        Entries.TryRemove(shortCode, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// An in-memory click buffer that behaves like the Redis one: clicks accumulate per link, a set of changed
/// links is kept, and popping or taking removes what it returns.
/// </summary>
public sealed class FakeClickBuffer : IClickBuffer
{
    private readonly object _gate = new();
    private readonly List<(string ShortCode, DateTimeOffset At)> _recorded = [];
    private readonly Dictionary<string, BufferedClicks> _pending = [];
    private readonly HashSet<string> _changed = [];

    /// <summary>Every click ever recorded, in order (not affected by taking or restoring).</summary>
    public IReadOnlyCollection<(string ShortCode, DateTimeOffset At)> Clicks
    {
        get
        {
            lock (_gate)
            {
                return _recorded.ToList();
            }
        }
    }

    /// <summary>When set, <see cref="RecordAsync"/> throws it (the real buffer never does; this proves callers cope).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>When set, <see cref="PopChangedCodesAsync"/> throws it, like a Redis outage.</summary>
    public Exception? FailPopWith { get; set; }

    /// <summary>When set, <see cref="RestoreAsync"/> throws it.</summary>
    public Exception? FailRestoreWith { get; set; }

    /// <summary>The clicks currently waiting for a flush, or null if there are none.</summary>
    public BufferedClicks? Pending(string shortCode)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(shortCode, out var clicks) ? clicks : null;
        }
    }

    /// <summary>How many links currently have clicks waiting to be flushed.</summary>
    public int PendingLinkCount
    {
        get
        {
            lock (_gate)
            {
                return _changed.Count;
            }
        }
    }

    public Task RecordAsync(string shortCode, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null)
        {
            return Task.FromException(FailWith);
        }

        lock (_gate)
        {
            _recorded.Add((shortCode, at));
            Add(shortCode, new BufferedClicks(1, at));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> PopChangedCodesAsync(int max, CancellationToken cancellationToken = default)
    {
        if (FailPopWith is not null)
        {
            return Task.FromException<IReadOnlyList<string>>(FailPopWith);
        }

        lock (_gate)
        {
            var codes = _changed.Take(max).ToList();
            _changed.ExceptWith(codes);
            return Task.FromResult<IReadOnlyList<string>>(codes);
        }
    }

    public Task<BufferedClicks?> TakeAsync(string shortCode, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_pending.Remove(shortCode, out var clicks) ? clicks : null);
        }
    }

    public Task RestoreAsync(string shortCode, BufferedClicks clicks, CancellationToken cancellationToken = default)
    {
        if (FailRestoreWith is not null)
        {
            return Task.FromException(FailRestoreWith);
        }

        lock (_gate)
        {
            Add(shortCode, clicks);
        }

        return Task.CompletedTask;
    }

    // Adds to the link's pending clicks (keeping the later timestamp) and marks it as changed.
    private void Add(string shortCode, BufferedClicks clicks)
    {
        _pending[shortCode] = _pending.TryGetValue(shortCode, out var existing)
            ? new BufferedClicks(existing.Count + clicks.Count, existing.LastAccessed > clicks.LastAccessed ? existing.LastAccessed : clicks.LastAccessed)
            : clicks;
        _changed.Add(shortCode);
    }
}
