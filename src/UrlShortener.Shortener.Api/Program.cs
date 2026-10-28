using System.Text.Json;
using System.Text.Json.Serialization;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Health;
using UrlShortener.Infrastructure.Telemetry;
using UrlShortener.Shortener.Api.Extensions;
using UrlShortener.Shortener.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Logs, traces and metrics (exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set).
builder.AddUrlShortenerObservability("shortener-api");

// A create-link request is a few hundred bytes; refuse anything much bigger before reading it.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 16 * 1024);

builder.Services.AddShortenerApi(builder.Configuration);

// The service cannot create links without Cassandra or without the Redis ID counter.
builder.Services.AddUrlShortenerHealthChecks(redisRequired: true);

builder.Services.AddControllers().AddJsonOptions(json =>
{
    var options = json.JsonSerializerOptions;
    options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // long_url, custom_alias, ...
    options.PropertyNameCaseInsensitive = false;
    options.NumberHandling = JsonNumberHandling.Strict;
    options.AllowDuplicateProperties = false;
});

// Errors are returned as RFC 7807 problem documents; storage outages become 503.
builder.Services.AddTransientFailureHandling();
builder.Services.AddOpenApi();

var app = builder.Build();

// Order matters: errors first, then identity, then rate limiting (which needs the identity), then permissions.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<UserLogScopeMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapUrlShortenerHealth();
app.MapControllers();

app.Run();

/// <summary>The application entry point. Public so integration tests can host the app in memory.</summary>
public partial class Program;
