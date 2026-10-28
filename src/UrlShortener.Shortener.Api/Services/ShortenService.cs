using Microsoft.Extensions.Options;
using UrlShortener.Core.Expiry;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Storage;
using UrlShortener.Core.Validation;
using UrlShortener.Infrastructure.Telemetry;
using UrlShortener.Shortener.Api.Configuration;

namespace UrlShortener.Shortener.Api.Services;

/// <inheritdoc />
internal sealed class ShortenService(
    IUrlMappingRepository links,
    IIdCounter counter,
    ShortCodeGenerator generator,
    LongUrlValidator urlValidator,
    CustomAliasValidator aliasValidator,
    ExpiryPolicy expiryPolicy,
    TimeProvider time,
    IOptions<ShortenerOptions> options,
    ILogger<ShortenService> logger) : IShortenService
{
    private readonly ShortenerOptions _options = options.Value;

    public async Task<ShortenResult> CreateAsync(
        string userId,
        string? longUrl,
        string? customAlias,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        var result = await CreateCoreAsync(userId, longUrl, customAlias, expiresAt, cancellationToken);

        UrlShortenerTelemetry.RecordShorten(result.Status switch
        {
            ShortenStatus.Created => "created",
            ShortenStatus.Invalid => "invalid",
            ShortenStatus.AliasTaken => "alias_taken",
            _ => "generation_failed",
        });

        if (result.Status == ShortenStatus.Created)
        {
            // The code and expiry only. The long URL can carry secrets in its query string, so it is never logged.
            logger.LogInformation(
                "Created short link {ShortCode} (custom alias: {IsCustom}) expiring {ExpiresAt:o}.",
                result.Link!.ShortCode,
                result.Link.IsCustom,
                result.Link.ExpiresAt);
        }

        return result;
    }

    private async Task<ShortenResult> CreateCoreAsync(
        string userId,
        string? longUrl,
        string? customAlias,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        // Check everything that can be checked without touching the database first.
        var urlCheck = urlValidator.Validate(longUrl);
        if (!urlCheck.IsValid)
        {
            return ShortenResult.Invalid("long_url", urlCheck.Error!);
        }

        // An empty alias means "none"; the API's sample request even sends "".
        var wantsCustomAlias = !string.IsNullOrEmpty(customAlias);
        if (wantsCustomAlias)
        {
            var aliasCheck = aliasValidator.Validate(customAlias);
            if (!aliasCheck.IsValid)
            {
                return ShortenResult.Invalid("custom_alias", aliasCheck.Error!);
            }
        }

        var expiry = expiryPolicy.Resolve(expiresAt);
        if (!expiry.IsValid)
        {
            return ShortenResult.Invalid("expires_at", expiry.Error!);
        }

        // Cassandra stores timestamps in milliseconds, so use the same precision in the response.
        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var expiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expiry.ExpiresAt.ToUnixTimeMilliseconds());
        var ttlSeconds = expiryPolicy.GetRowTtlSeconds(expiresAtUtc);

        UrlMapping NewLink(string shortCode) =>
            new(shortCode, longUrl!, userId, createdAt, expiresAtUtc, IsCustom: wantsCustomAlias);

        if (wantsCustomAlias)
        {
            var link = NewLink(customAlias!);
            return await links.TryInsertAsync(link, ttlSeconds, cancellationToken)
                ? ShortenResult.Created(link, BuildShortUrl(link.ShortCode))
                : ShortenResult.AliasTaken;
        }

        return await CreateWithGeneratedCodeAsync(NewLink, ttlSeconds, cancellationToken);
    }

    // A generated code can still collide with a custom alias someone already took, or with a number
    // reissued after the counter was restored. The conditional insert detects it; just try the next number.
    private async Task<ShortenResult> CreateWithGeneratedCodeAsync(
        Func<string, UrlMapping> newLink,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < _options.MaxGenerateAttempts; attempt++)
        {
            var number = await counter.NextAsync(cancellationToken);
            var link = newLink(generator.Generate(number));

            if (await links.TryInsertAsync(link, ttlSeconds, cancellationToken))
            {
                return ShortenResult.Created(link, BuildShortUrl(link.ShortCode));
            }
        }

        return ShortenResult.CodeGenerationFailed;
    }

    private string BuildShortUrl(string shortCode) => $"{_options.ShortUrlBase.ToString().TrimEnd('/')}/{shortCode}";
}
