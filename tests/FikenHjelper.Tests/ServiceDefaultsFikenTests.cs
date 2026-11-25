using System.Diagnostics;
using System.Net;
using System.Text;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FikenHjelper.Tests;

/// <summary>
/// Sikrer at Aspire ServiceDefaults ikke legger standard resilience (retries/hedging) på Fiken-klienten,
/// og at FikenRateLimitHandler fortsatt håndhever maks 1 samtidig kall med minst 260 ms mellomrom.
/// </summary>
public sealed class ServiceDefaultsFikenTests
{
    [Fact]
    public async Task ServiceDefaults_DoesNotRetryServerErrorsOnFikenClient()
    {
        var callCount = 0;
        using var host = BuildHost((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":"unavailable"}""", Encoding.UTF8, "application/json")
            });
        });

        var client = host.Services.GetRequiredService<IFikenClient>();

        await Assert.ThrowsAsync<FikenApiException>(() => client.GetCompaniesAsync());
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ServiceDefaults_KeepsFikenRateLimitHandlerOwn429Retries()
    {
        var callCount = 0;
        using var host = BuildHost((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });

        var client = host.Services.GetRequiredService<IFikenClient>();

        await Assert.ThrowsAsync<FikenApiException>(() => client.GetCompaniesAsync());
        Assert.Equal(3, callCount); // 1 forsøk + 2 egne 429-retries i FikenRateLimitHandler
    }

    [Fact]
    public async Task ServiceDefaults_FikenCallsStaySerializedAndSpaced()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var startTimes = new List<TimeSpan>();
        var clock = Stopwatch.StartNew();
        var sync = new object();

        using var host = BuildHost(async (_, ct) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            lock (sync)
            {
                maxInFlight = Math.Max(maxInFlight, current);
                startTimes.Add(clock.Elapsed);
            }

            await Task.Delay(20, ct);
            Interlocked.Decrement(ref inFlight);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });

        var client = host.Services.GetRequiredService<IFikenClient>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.GetCompaniesAsync()));

        Assert.Equal(1, maxInFlight);
        Assert.Equal(4, startTimes.Count);
        var ordered = startTimes.Order().ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            // Minst 260 ms etter forrige fullførte kall (20 ms) – tillat litt timer-unøyaktighet.
            Assert.True(ordered[i] - ordered[i - 1] >= TimeSpan.FromMilliseconds(250),
                $"Kall {i} startet bare {(ordered[i] - ordered[i - 1]).TotalMilliseconds:F0} ms etter forrige.");
        }
    }

    private static IHost BuildHost(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            DisableDefaults = true
        });

        builder.AddServiceDefaults();

        var dataDir = Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton<ILocalConfigStore>(
            new LocalConfigStore(new AppSettingsDefaults(FikenApiToken: "test-token"), dataDir));

        builder.Services.AddFikenApiClient()
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler(responder));

        return builder.Build();
    }
}
