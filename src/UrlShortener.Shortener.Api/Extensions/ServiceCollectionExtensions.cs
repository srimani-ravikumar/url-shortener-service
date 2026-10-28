using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using UrlShortener.Core.Expiry;
using UrlShortener.Core.ShortCodes;
using UrlShortener.Core.Validation;
using UrlShortener.Infrastructure;
using UrlShortener.Shortener.Api.Authentication;
using UrlShortener.Shortener.Api.Configuration;
using UrlShortener.Shortener.Api.Services;

namespace UrlShortener.Shortener.Api.Extensions;

/// <summary>Registers everything the shortener service needs, so Program.cs stays short.</summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddShortenerApi(this IServiceCollection services, IConfiguration configuration)
    {
        // Bad configuration (e.g. a missing secret) should stop the service at startup, not break a request later.
        services.AddOptions<ShortenerOptions>()
            .Bind(configuration.GetSection(ShortenerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.Configure<ExpiryOptions>(configuration.GetSection("Expiry"));

        services.AddUrlShortenerInfrastructure(configuration);
        services.AddDomainServices();
        services.AddApiKeyAuthentication();
        services.AddShortenRateLimiting();

        return services;
    }

    // The pure logic from UrlShortener.Core, configured from settings.
    private static void AddDomainServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
            FeistelObfuscator.FromSecret(provider.GetRequiredService<IOptions<ShortenerOptions>>().Value.FeistelSecret));
        services.AddSingleton<ShortCodeGenerator>();
        services.AddSingleton(provider =>
            new LongUrlValidator(provider.GetRequiredService<IOptions<ShortenerOptions>>().Value.ShortUrlBase));
        services.AddSingleton<CustomAliasValidator>();
        services.AddSingleton(provider => new ExpiryPolicy(
            provider.GetRequiredService<IOptions<ExpiryOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IShortenService, ShortenService>();
        services.AddSingleton<ILinkQueryService, LinkQueryService>();
        services.AddHostedService<CounterRecoveryService>();
    }

    private static void AddApiKeyAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, configureOptions: null);

        // Secure by default: every endpoint needs a valid key unless it is explicitly marked [AllowAnonymous].
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    }

    private static void AddShortenRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(RateLimiting.ShortenPolicy, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;

                // Each user gets their own bucket. Requests without a valid key share a bucket per client
                // address, so guessing keys is throttled as well.
                var partition = httpContext.User.Identity?.IsAuthenticated == true
                    ? "user:" + httpContext.User.GetUserId()
                    : "ip:" + httpContext.Connection.RemoteIpAddress;

                return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.TokenLimit,
                    TokensPerPeriod = settings.TokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(settings.ReplenishmentPeriodSeconds),
                    QueueLimit = 0, // reject immediately instead of making the caller wait
                    AutoReplenishment = true,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var httpContext = context.HttpContext;
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    httpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = httpContext,
                    ProblemDetails =
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = "You are creating links too quickly. Wait a moment and try again.",
                    },
                });
            };
        });
    }
}
