using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace UrlShortener.Infrastructure.Telemetry;

/// <summary>Sets up logging, tracing and metrics the same way for every service.</summary>
public static class ObservabilityExtensions
{
    /// <summary>
    /// Configures log format and OpenTelemetry for a service.
    /// <list type="bullet">
    /// <item>Logs are JSON on the console (readable text in Development; force either with <c>Logging:Json</c>).</item>
    /// <item>Traces, metrics and logs are exported over OTLP, but only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is
    /// configured (e.g. http://localhost:4317). Without it nothing is collected, so a plain <c>dotnet run</c> and the
    /// tests stay quiet and do not try to reach a collector.</item>
    /// </list>
    /// </summary>
    /// <param name="builder">The host or web application builder.</param>
    /// <param name="serviceName">The name shown in Grafana, e.g. "redirector-api" (OTEL_SERVICE_NAME overrides it).</param>
    public static TBuilder AddUrlShortenerObservability<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        ConfigureConsole(builder);

        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return builder;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health probes run every few seconds; they would drown out real traffic.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    options.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                .AddSource(UrlShortenerTelemetry.Name)
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(UrlShortenerTelemetry.Name)
                .AddOtlpExporter());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddOtlpExporter();
        });

        return builder;
    }

    private static void ConfigureConsole(IHostApplicationBuilder builder)
    {
        var json = builder.Configuration.GetValue<bool?>("Logging:Json") ?? !builder.Environment.IsDevelopment();
        if (!json)
        {
            return;
        }

        // One JSON object per line, with scopes (trace id, span id, user id), so log tools can filter on fields.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
    }
}
