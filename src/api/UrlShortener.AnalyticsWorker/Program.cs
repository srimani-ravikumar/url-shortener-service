using Microsoft.AspNetCore.Builder;
using UrlShortener.AnalyticsWorker.Extensions;
using UrlShortener.Infrastructure.Health;
using UrlShortener.Infrastructure.Telemetry;

// A web host (without MVC) so the worker can answer health probes on a small HTTP port.
var builder = WebApplication.CreateSlimBuilder(args);

// Logs, traces and metrics (exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set).
builder.AddUrlShortenerObservability("analytics-worker");
builder.Services.AddAnalyticsWorker(builder.Configuration);

var app = builder.Build();
app.MapUrlShortenerHealth();
app.Run();

/// <summary>The application entry point. Public so tests can start the worker in memory.</summary>
public partial class Program;
