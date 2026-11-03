using Microsoft.Extensions.Options;
using UrlShortener.Infrastructure.Health;
using UrlShortener.Infrastructure.Telemetry;
using UrlShortener.Redirector.Api.Configuration;
using UrlShortener.Redirector.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Logs, traces and metrics (exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set).
builder.AddUrlShortenerObservability("redirector-api");

builder.Services.AddRedirectorApi(builder.Configuration);
builder.Services.AddControllers();

// Cassandra is required. Redis is not: without it redirects still work (from Cassandra), so it only degrades.
builder.Services.AddUrlShortenerHealthChecks(redisRequired: false);

var app = builder.Build();

// Development aid (off by default): name the replica that answered, to check load balancing.
if (app.Services.GetRequiredService<IOptions<RedirectorOptions>>().Value.ExposeInstanceId)
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Served-By"] = Environment.MachineName;
        await next();
    });
}

// No authentication: short links are public. Errors are problem documents; storage outages become 503.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapUrlShortenerHealth();
app.MapControllers();

app.Run();

/// <summary>The application entry point. Public so integration tests can host the app in memory.</summary>
public partial class Program;
