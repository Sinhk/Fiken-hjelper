using System.Net;
using System.Text;
using System.Web;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class FikenOAuthServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "fiken-oauth-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ComputeS256Challenge_MatchesFikenRfc7636Example()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        const string expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var challenge = FikenPkceHelper.ComputeS256Challenge(verifier);

        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void BuildAuthorizationUrl_ContainsAllRequiredOAuthAndPkceParameters()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        using var oauth = new FikenOAuthService(http, store);

        var url = oauth.BuildAuthorizationUrl(
            "client-123",
            "https://localhost:7193/fiken/oauth/callback",
            "state-xyz",
            "challenge-abc");

        var uri = new Uri(url);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("fiken.no", uri.Host);
        Assert.Equal("/oauth/authorize", uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-123", query["client_id"]);
        Assert.Equal("https://localhost:7193/fiken/oauth/callback", query["redirect_uri"]);
        Assert.Equal("state-xyz", query["state"]);
        Assert.Equal("challenge-abc", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_PublicPkceClient_OmitsBasicAuthAndPersistsTokens()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            FikenOAuthClientId = "public-client-id",
            FikenOAuthClientSecret = ""
        });

        string? capturedForm = null;
        bool hadAuthHeader = false;
        var handler = new StubHttpHandler(async (req, ct) =>
        {
            hadAuthHeader = req.Headers.Authorization is not null;
            capturedForm = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"acc-1","refresh_token":"ref-1","token_type":"bearer","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var oauth = new FikenOAuthService(http, store);

        var tokenRes = await oauth.ExchangeAuthorizationCodeAsync(
            "auth-code-1",
            "verifier-1",
            "http://localhost:5066/fiken/oauth/callback",
            "state-1");

        Assert.False(hadAuthHeader);
        Assert.NotNull(capturedForm);
        var parsed = HttpUtility.ParseQueryString(capturedForm);
        Assert.Equal("authorization_code", parsed["grant_type"]);
        Assert.Equal("public-client-id", parsed["client_id"]);
        Assert.Equal("auth-code-1", parsed["code"]);
        Assert.Equal("verifier-1", parsed["code_verifier"]);
        Assert.Equal("acc-1", tokenRes.AccessToken);

        var loaded = await store.LoadSettingsAsync();
        Assert.Equal("acc-1", loaded.FikenOAuthAccessToken);
        Assert.Equal("ref-1", loaded.FikenOAuthRefreshToken);
        Assert.NotNull(loaded.FikenOAuthExpiresAtUtc);
    }

    [Fact]
    public async Task ExchangeAuthorizationCodeAsync_ConfidentialClient_SendsBasicAuthHeaderAndOmitsClientIdInBody()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            FikenOAuthClientId = "conf-id",
            FikenOAuthClientSecret = "conf-secret"
        });

        string? authScheme = null;
        string? authParam = null;
        string? capturedForm = null;

        var handler = new StubHttpHandler(async (req, ct) =>
        {
            authScheme = req.Headers.Authorization?.Scheme;
            authParam = req.Headers.Authorization?.Parameter;
            capturedForm = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"acc-2","refresh_token":"ref-2","token_type":"bearer","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var oauth = new FikenOAuthService(http, store);

        await oauth.ExchangeAuthorizationCodeAsync(
            "auth-code-2",
            "verifier-2",
            "https://localhost:7193/fiken/oauth/callback",
            "state-2");

        Assert.Equal("Basic", authScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("conf-id:conf-secret")), authParam);

        var parsed = HttpUtility.ParseQueryString(capturedForm!);
        Assert.Null(parsed["client_id"]);
        Assert.Equal("verifier-2", parsed["code_verifier"]);
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_ReturnsCachedWhenFresh_AndSerializesRotatedRefreshWhenExpired()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            FikenOAuthClientId = "public-client-id"
        });
        await store.SaveFikenOAuthTokensAsync(
            "fresh-token",
            "refresh-1",
            DateTimeOffset.UtcNow.AddHours(2));

        var refreshCallCount = 0;
        var handler = new StubHttpHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref refreshCallCount);
            await Task.Delay(30, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"rotated-access","refresh_token":"rotated-refresh","token_type":"bearer","expires_in":7200}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var http = new HttpClient(handler);
        using var oauth = new FikenOAuthService(http, store);

        // Fresh token: no HTTP refresh should occur
        var cached = await oauth.GetValidAccessTokenAsync();
        Assert.Equal("fresh-token", cached);
        Assert.Equal(0, refreshCallCount);

        // Simulate near-expiry token: concurrent callers should trigger ONLY ONE refresh call
        await store.SaveFikenOAuthTokensAsync(
            "expiring-token",
            "refresh-1",
            DateTimeOffset.UtcNow.AddSeconds(30));

        var tokens = await Task.WhenAll(
            oauth.GetValidAccessTokenAsync(),
            oauth.GetValidAccessTokenAsync(),
            oauth.GetValidAccessTokenAsync(),
            oauth.GetValidAccessTokenAsync());

        Assert.Equal(1, refreshCallCount);
        Assert.All(tokens, t => Assert.Equal("rotated-access", t));

        var savedAfter = await store.LoadSettingsAsync();
        Assert.Equal("rotated-access", savedAfter.FikenOAuthAccessToken);
        Assert.Equal("rotated-refresh", savedAfter.FikenOAuthRefreshToken);
    }

    [Fact]
    public async Task FikenClient_RefreshesTokenAndRetriesOnceOn401()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings
        {
            FikenOAuthClientId = "public-client-id"
        });
        await store.SaveFikenOAuthTokensAsync(
            "stale-token",
            "refresh-1",
            DateTimeOffset.UtcNow.AddHours(1));

        var oauthHandler = new StubHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"new-token","refresh_token":"refresh-2","token_type":"bearer","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json")
            }));

        using var oauthHttp = new HttpClient(oauthHandler);
        using var oauthService = new FikenOAuthService(oauthHttp, store);

        var apiCalls = 0;
        var fikenHandler = new StubHttpHandler((req, _) =>
        {
            apiCalls++;
            if (req.Headers.Authorization?.Parameter == "stale-token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            Assert.Equal("new-token", req.Headers.Authorization?.Parameter);
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"name":"Romsdal og Nordmøre krets","slug":"romsdal-krets","hasApiAccess":true,"testCompany":false}]""",
                    Encoding.UTF8,
                    "application/json")
            };
            res.Headers.Add("Fiken-Api-Page-Count", "1");
            return Task.FromResult(res);
        });

        using var fikenHttp = new HttpClient(fikenHandler) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
        var fikenClient = new FikenClient(fikenHttp, store, oauthService);

        var companies = await fikenClient.GetCompaniesAsync();

        Assert.Equal(2, apiCalls);
        Assert.Single(companies);
        Assert.Equal("romsdal-krets", companies[0].Slug);
    }

    [Fact]
    public async Task LocalConfigStore_SaveSettingsAsync_PreservesExistingOAuthTokens()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        var expires = DateTimeOffset.UtcNow.AddHours(6);
        await store.SaveFikenOAuthTokensAsync("keep-access", "keep-refresh", expires);

        await store.SaveSettingsAsync(new AppSettings
        {
            FikenOAuthClientId = "client-123",
            FikenCompanySlug = "nytt-foretak"
        });

        var loaded = await store.LoadSettingsAsync();
        Assert.Equal("client-123", loaded.FikenOAuthClientId);
        Assert.Equal("nytt-foretak", loaded.FikenCompanySlug);
        Assert.Equal("keep-access", loaded.FikenOAuthAccessToken);
        Assert.Equal("keep-refresh", loaded.FikenOAuthRefreshToken);
        Assert.Equal(expires, loaded.FikenOAuthExpiresAtUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
