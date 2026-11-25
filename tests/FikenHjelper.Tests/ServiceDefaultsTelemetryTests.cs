using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FikenHjelper.Tests;

public sealed class ServiceDefaultsTelemetryTests
{
    [Fact]
    public async Task ServiceDefaults_RedactsIncomingQueryStringEvenWhenAspireDisablesRedaction()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        // Simulerer miljøvariabelen Aspire setter på prosjektressurser.
        builder.Configuration["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = "true";
        builder.Configuration["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"] = "true";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddServiceDefaults();

        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Microsoft.AspNetCore" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = builder.Build();
        app.MapGet("/fiken/oauth/callback", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);

        // Bruk appens egen IHttpClientFactory, slik at utgående kall går gjennom ServiceDefaults-oppsettet.
        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
        var body = await http.GetStringAsync(
            $"{app.Urls.First()}/fiken/oauth/callback?code=SECRET-CODE-123&state=SECRET-STATE-456",
            TestContext.Current.CancellationToken);
        Assert.Equal("ok", body);

        await app.StopAsync(TestContext.Current.CancellationToken);

        var serverActivity = Assert.Single(stopped, a => a.Kind == ActivityKind.Server);
        var clientActivity = Assert.Single(stopped, a => a.Kind == ActivityKind.Client);
        foreach (var activity in new[] { serverActivity, clientActivity })
        {
            foreach (var tag in activity.TagObjects)
            {
                Assert.DoesNotContain("SECRET", tag.Value?.ToString() ?? string.Empty);
            }
        }

        Assert.Equal("?Redacted", serverActivity.GetTagItem("url.query"));
        // .NET redigerer selv url.full til "?*"; "?Redacted" viser at vår egen enrichment faktisk kjørte.
        Assert.EndsWith("?Redacted", clientActivity.GetTagItem("url.full")?.ToString());
        Assert.DoesNotContain("SECRET", serverActivity.DisplayName);
    }
}
