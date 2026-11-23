using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddServiceDefaults();

        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);

        await using var app = builder.Build();
        app.MapGet("/fiken/oauth/callback", () => "ok");
        await app.StartAsync();

        using var http = new HttpClient();
        var body = await http.GetStringAsync(
            $"{app.Urls.First()}/fiken/oauth/callback?code=SECRET-CODE-123&state=SECRET-STATE-456");
        Assert.Equal("ok", body);

        await app.StopAsync();

        var serverActivity = Assert.Single(stopped, a => a.Kind == ActivityKind.Server);
        foreach (var tag in serverActivity.TagObjects)
        {
            var value = tag.Value?.ToString() ?? string.Empty;
            Assert.DoesNotContain("SECRET", value);
        }

        Assert.Equal("?Redacted", serverActivity.GetTagItem("url.query"));
        Assert.DoesNotContain("SECRET", serverActivity.DisplayName);
    }
}
