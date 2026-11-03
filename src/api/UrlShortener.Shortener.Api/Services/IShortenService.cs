using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Services;

/// <summary>How a request to create a short link ended.</summary>
public enum ShortenStatus
{
    /// <summary>The link was stored.</summary>
    Created,

    /// <summary>The request itself is wrong (bad URL, alias or expiry).</summary>
    Invalid,

    /// <summary>The requested custom alias already belongs to another link.</summary>
    AliasTaken,

    /// <summary>Every generated code collided with an existing one, which should be extremely rare.</summary>
    CodeGenerationFailed,
}

/// <summary>
/// Outcome of creating a short link. Expected failures are returned as values instead of exceptions,
/// so the controller can turn each one into the right HTTP status.
/// </summary>
public sealed record ShortenResult
{
    /// <summary>What happened; decides which of the other properties are set.</summary>
    public required ShortenStatus Status { get; init; }

    /// <summary>The created link (only when <see cref="Status"/> is Created).</summary>
    public UrlMapping? Link { get; init; }

    /// <summary>The short URL to share (only when Created).</summary>
    public string? ShortUrl { get; init; }

    /// <summary>The request field at fault, e.g. "long_url" (only when Invalid).</summary>
    public string? Field { get; init; }

    /// <summary>A message safe to show to the caller (only when Invalid).</summary>
    public string? Error { get; init; }

    /// <summary>Result for a stored link.</summary>
    public static ShortenResult Created(UrlMapping link, string shortUrl) =>
        new() { Status = ShortenStatus.Created, Link = link, ShortUrl = shortUrl };

    /// <summary>Result for a request with a bad field.</summary>
    public static ShortenResult Invalid(string field, string error) =>
        new() { Status = ShortenStatus.Invalid, Field = field, Error = error };

    /// <summary>Result when the custom alias is already used.</summary>
    public static ShortenResult AliasTaken { get; } = new() { Status = ShortenStatus.AliasTaken };

    /// <summary>Result when no free generated code was found.</summary>
    public static ShortenResult CodeGenerationFailed { get; } = new() { Status = ShortenStatus.CodeGenerationFailed };
}

/// <summary>Creates short links.</summary>
public interface IShortenService
{
    /// <summary>Validates the request and stores a new short link owned by <paramref name="userId"/>.</summary>
    Task<ShortenResult> CreateAsync(
        string userId,
        string? longUrl,
        string? customAlias,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken);
}
