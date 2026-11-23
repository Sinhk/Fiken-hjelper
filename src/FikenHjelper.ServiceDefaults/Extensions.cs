using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Felles Aspire-standarder for Fiken-hjelper: OpenTelemetry, helsesjekker og tjenesteoppdagelse.
/// Avviker bevisst fra Aspire-malen ved å IKKE legge standard resilience på alle HttpClients,
/// fordi Fiken API krever maks 1 samtidig kall og maks 4 kall per sekund
/// (håndheves av <c>FikenRateLimitHandler</c> i FikenHjelper.Core).
/// </summary>
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    private const string UrlQueryTag = "url.query";
    private const string UrlFullTag = "url.full";
    private const string RedactedQuery = "?Redacted";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // IKKE http.AddStandardResilienceHandler() her: parallelle/aggressive retries og hedging
            // kan bryte Fikens rate-limit. FikenClient har sin egen sekvensielle FikenRateLimitHandler.

            // Tjenesteoppdagelse er ufarlig for absolutte URI-er (api.fiken.no, min.speiding.no).
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Hosting-loggen ("Request starting ... ?code=...&state=...") skal aldri eksporteres til dashboardet,
        // selv om noen senker loggnivået lokalt.
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !IsNoisyRequest(context.Request.Path);

                        // Aspire slår av query-redigering (OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION=true).
                        // Vi redigerer likevel alltid, slik at f.eks. OAuth-callbackens code/state aldri havner i traces.
                        options.EnrichWithHttpRequest = (activity, request) =>
                        {
                            if (request.QueryString.HasValue)
                            {
                                activity.SetTag(UrlQueryTag, RedactedQuery);
                            }
                        };
                    })
                    .AddHttpClientInstrumentation(options =>
                    {
                        // Aspire slår også av query-redigering for utgående kall. Fjern query fra url.full,
                        // så eventuelle nøkler i URL-er aldri havner i traces.
                        options.EnrichWithHttpRequestMessage = (activity, request) =>
                        {
                            if (request.RequestUri is { Query.Length: > 0 } uri)
                            {
                                activity.SetTag(UrlFullTag, uri.GetLeftPart(UriPartial.Path) + RedactedQuery);
                            }
                        };
                    });
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static bool IsNoisyRequest(PathString path) =>
        path.StartsWithSegments(HealthEndpointPath)
        || path.StartsWithSegments(AlivenessEndpointPath)
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_content")
        || Path.HasExtension(path.Value);

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Eksporterer kun når Aspire (eller annen OTLP-mottaker) har satt endepunktet.
        // Startes appen uten Aspire, sendes ingen telemetri noe sted.
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Ingen helsesjekk mot Fiken: den ville brukt av rate-limit-kvoten og krever innlogging.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Helsesjekk-endepunkter eksponeres kun i Development (se https://aka.ms/aspire/healthchecks).
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath);

            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
