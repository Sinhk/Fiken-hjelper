using System.Net;
using System.Text;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

/// <summary>
/// Enkel falsk Fiken-API for tester: svar registreres per metode + sti (uten query), og alle kall logges.
/// </summary>
internal sealed class FakeFikenApi : IDisposable
{
    private readonly Dictionary<string, Func<HttpRequestMessage, string?, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Calls { get; } = [];

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "fh-fake-" + Guid.NewGuid().ToString("N"));

    public HttpClient Http { get; }

    public FakeFikenApi()
    {
        Http = new HttpClient(new StubHttpHandler(HandleAsync)) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
    }

    public FakeFikenApi Get(string path, string json) =>
        On(HttpMethod.Get, path, (_, _) => Json(json));

    public FakeFikenApi On(HttpMethod method, string path, Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        _routes[$"{method} /api/v2/{path}"] = responder;
        return this;
    }

    public LocalConfigStore CreateStore(string companySlug = "test-as") =>
        new(new AppSettingsDefaults(FikenApiToken: "test-token", FikenCompanySlug: companySlug), DataDirectory);

    public FikenClient CreateClient(ILocalConfigStore? store = null) => new(Http, store ?? CreateStore());

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Created(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!.PathAndQuery, body));
        var key = $"{request.Method} {request.RequestUri.AbsolutePath}";
        if (_routes.TryGetValue(key, out var responder))
        {
            return responder(request, body);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"Ingen falsk rute for {key}")
        };
    }

    public void Dispose()
    {
        Http.Dispose();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
