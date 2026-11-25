using System.Net;
using System.Text;
using System.Web;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

/// <summary>
/// Simulerer at Web og MCP-serveren kjører samtidig: separate instanser (som i separate prosesser)
/// som deler samme datamappe.
/// </summary>
public sealed class CrossProcessLockTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "fiken-lock-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileLock_SecondAcquireWaitsUntilFirstIsReleased()
    {
        var path = Path.Combine(_tempDir, "test.lock");
        var first = await CrossProcessFileLock.AcquireAsync(path, TestContext.Current.CancellationToken);

        var secondTask = CrossProcessFileLock.AcquireAsync(path, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(secondTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FileLock_TimesOutWithClearMessage()
    {
        var path = Path.Combine(_tempDir, "timeout.lock");
        using var first = await CrossProcessFileLock.AcquireAsync(path, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            CrossProcessFileLock.AcquireAsync(path, timeout: TimeSpan.FromMilliseconds(100), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FileLock_StoresSharedValue()
    {
        var path = Path.Combine(_tempDir, "value.lock");
        using (var fileLock = await CrossProcessFileLock.AcquireAsync(path, TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, fileLock.ReadInt64());
            fileLock.WriteInt64(123456789L);
        }

        using var again = await CrossProcessFileLock.AcquireAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(123456789L, again.ReadInt64());
    }

    [Fact]
    public async Task SharedRateLimiters_WithSameLockFile_NeverOverlapAndKeepSpacing()
    {
        var spacing = TimeSpan.FromMilliseconds(40);
        var lockPath = Path.Combine(_tempDir, FikenRateLimiter.LockFileName);
        using var limiterA = new FikenRateLimiter(spacing, lockPath);
        using var limiterB = new FikenRateLimiter(spacing, lockPath);

        var active = 0;
        var maxActive = 0;
        var gate = new object();
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        StubHttpHandler CreateInner() => new(async (_, ct) =>
        {
            var start = DateTimeOffset.UtcNow;
            var current = Interlocked.Increment(ref active);
            lock (gate)
            {
                maxActive = Math.Max(maxActive, current);
            }

            try
            {
                await Task.Delay(15, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref active);
                lock (gate)
                {
                    intervals.Add((start, DateTimeOffset.UtcNow));
                }
            }
        });

        using var httpA = new HttpClient(new FikenRateLimitHandler(limiterA) { InnerHandler = CreateInner() })
        {
            BaseAddress = new Uri(FikenClient.DefaultBaseAddress)
        };
        using var httpB = new HttpClient(new FikenRateLimitHandler(limiterB) { InnerHandler = CreateInner() })
        {
            BaseAddress = new Uri(FikenClient.DefaultBaseAddress)
        };

        var tasks = Enumerable.Range(0, 6)
            .Select(i => (i % 2 == 0 ? httpA : httpB).GetAsync("companies"))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, maxActive);

        var ordered = intervals.OrderBy(i => i.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].Start - ordered[i - 1].End;
            // Litt slingringsmonn for timer-oppløsning.
            Assert.True(gap >= spacing - TimeSpan.FromMilliseconds(10), $"For kort mellomrom: {gap.TotalMilliseconds} ms");
        }
    }

    [Fact]
    public async Task OAuthRefresh_TwoProcessesWithExpiredToken_RefreshOnceAndStayLoggedIn()
    {
        await SeedExpiredTokensAsync();

        var tokenCalls = 0;
        var currentRefresh = "ref-1";
        var issued = 0;
        var serverGate = new object();

        StubHttpHandler CreateTokenEndpoint() => new(async (req, ct) =>
        {
            var form = HttpUtility.ParseQueryString(await req.Content!.ReadAsStringAsync(ct));
            Interlocked.Increment(ref tokenCalls);
            await Task.Delay(50, ct);
            lock (serverGate)
            {
                if (form["refresh_token"] != currentRefresh)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
                    };
                }

                issued++;
                currentRefresh = $"ref-{issued + 1}";
                return TokenResponse($"acc-{issued + 1}", currentRefresh);
            }
        });

        // To separate "prosesser": egne config-store-, OAuth- og HttpClient-instanser, samme datamappe.
        var storeWeb = new LocalConfigStore(dataDirectory: _tempDir);
        var storeMcp = new LocalConfigStore(dataDirectory: _tempDir);
        using var httpWeb = new HttpClient(CreateTokenEndpoint());
        using var httpMcp = new HttpClient(CreateTokenEndpoint());
        using var oauthWeb = new FikenOAuthService(httpWeb, storeWeb);
        using var oauthMcp = new FikenOAuthService(httpMcp, storeMcp);

        var results = await Task.WhenAll(
            oauthWeb.GetValidAccessTokenAsync(ct: TestContext.Current.CancellationToken),
            oauthMcp.GetValidAccessTokenAsync(ct: TestContext.Current.CancellationToken));

        Assert.Equal(1, tokenCalls);
        Assert.All(results, token => Assert.Equal("acc-2", token));

        var loaded = await storeWeb.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("acc-2", loaded.FikenOAuthAccessToken);
        Assert.Equal("ref-2", loaded.FikenOAuthRefreshToken);
    }

    [Fact]
    public async Task OAuthRefresh_RejectedButRotatedByOtherProcess_UsesNewTokenInsteadOfLoggingOut()
    {
        await SeedExpiredTokensAsync();
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        var otherProcessStore = new LocalConfigStore(dataDirectory: _tempDir);

        // En annen prosess (uten låsfil) roterer tokenet akkurat før Fiken avviser vårt gamle refresh-token.
        using var http = new HttpClient(new StubHttpHandler(async (_, ct) =>
        {
            await otherProcessStore.SaveFikenOAuthTokensAsync("acc-other", "ref-other", DateTimeOffset.UtcNow.AddHours(1), ct);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
            };
        }));
        using var oauth = new FikenOAuthService(http, store);

        var token = await oauth.GetValidAccessTokenAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal("acc-other", token);
        var loaded = await store.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ref-other", loaded.FikenOAuthRefreshToken);
    }

    [Fact]
    public async Task OAuthRefresh_RejectedWithoutRotation_ClearsTokensAndThrows()
    {
        await SeedExpiredTokensAsync();
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
            })));
        using var oauth = new FikenOAuthService(http, store);

        var ex = await Assert.ThrowsAsync<FikenApiException>(() => oauth.GetValidAccessTokenAsync(ct: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        var loaded = await store.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.False(loaded.HasFikenOAuthTokens);
    }

    [Fact]
    public async Task OAuthForceRefresh_FromTwoProcessesAtOnce_NeitherIsLoggedOut()
    {
        var seed = new LocalConfigStore(dataDirectory: _tempDir);
        await seed.SaveSettingsAsync(new AppSettings { FikenOAuthClientId = "client" }, TestContext.Current.CancellationToken);
        await seed.SaveFikenOAuthTokensAsync("acc-1", "ref-1", DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);

        var currentRefresh = "ref-1";
        var issued = 0;
        var serverGate = new object();
        StubHttpHandler CreateTokenEndpoint() => new(async (req, ct) =>
        {
            var form = HttpUtility.ParseQueryString(await req.Content!.ReadAsStringAsync(ct));
            await Task.Delay(30, ct);
            lock (serverGate)
            {
                if (form["refresh_token"] != currentRefresh)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
                    };
                }

                issued++;
                currentRefresh = $"ref-{issued + 1}";
                return TokenResponse($"acc-{issued + 1}", currentRefresh);
            }
        });

        var storeA = new LocalConfigStore(dataDirectory: _tempDir);
        var storeB = new LocalConfigStore(dataDirectory: _tempDir);
        using var httpA = new HttpClient(CreateTokenEndpoint());
        using var httpB = new HttpClient(CreateTokenEndpoint());
        using var oauthA = new FikenOAuthService(httpA, storeA);
        using var oauthB = new FikenOAuthService(httpB, storeB);

        // Begge har fått 401 på acc-1 og ber om tvungen fornyelse samtidig.
        var results = await Task.WhenAll(
            oauthA.GetValidAccessTokenAsync(forceRefresh: true, TestContext.Current.CancellationToken),
            oauthB.GetValidAccessTokenAsync(forceRefresh: true, TestContext.Current.CancellationToken));

        Assert.All(results, token => Assert.StartsWith("acc-", token));
        Assert.DoesNotContain("acc-1", results);
        var loaded = await storeA.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.True(loaded.HasFikenOAuthTokens);
        Assert.Equal(currentRefresh, loaded.FikenOAuthRefreshToken);
    }

    [Fact]
    public async Task ConfigStore_SettingsSavedInOneProcess_DoNotClobberTokensRotatedInAnother()
    {
        var storeWeb = new LocalConfigStore(dataDirectory: _tempDir);
        var storeMcp = new LocalConfigStore(dataDirectory: _tempDir);
        await storeWeb.SaveSettingsAsync(new AppSettings { FikenOAuthClientId = "client", FikenCompanySlug = "krets" }, TestContext.Current.CancellationToken);

        const int iterations = 40;
        var settingsWriter = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await storeWeb.SaveSettingsAsync(new AppSettings { FikenOAuthClientId = "client", FikenCompanySlug = $"krets-{i}" });
            }
        }, TestContext.Current.CancellationToken);
        var tokenWriter = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await storeMcp.SaveFikenOAuthTokensAsync($"acc-{i}", $"ref-{i}", DateTimeOffset.UtcNow.AddHours(1));
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(settingsWriter, tokenWriter);

        var loaded = await storeWeb.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal($"ref-{iterations - 1}", loaded.FikenOAuthRefreshToken);
        Assert.Equal($"krets-{iterations - 1}", loaded.FikenCompanySlug);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    private async Task SeedExpiredTokensAsync()
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings { FikenOAuthClientId = "client" });
        await store.SaveFikenOAuthTokensAsync("acc-1", "ref-1", DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private static HttpResponseMessage TokenResponse(string access, string refresh) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","token_type":"bearer","expires_in":3600}""",
                Encoding.UTF8,
                "application/json")
        };

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}

public sealed class OAuthReviewFixTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "fiken-oauth-fix-" + Guid.NewGuid().ToString("N"));

    private async Task<LocalConfigStore> SeedAsync(DateTimeOffset expires)
    {
        var store = new LocalConfigStore(dataDirectory: _tempDir);
        await store.SaveSettingsAsync(new AppSettings { FikenOAuthClientId = "client" });
        await store.SaveFikenOAuthTokensAsync("acc-1", "ref-1", expires);
        return store;
    }

    [Fact]
    public async Task RefreshFailingWithInvalidClient_KeepsTokens()
    {
        var store = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        using var http = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"invalid_client"}""", Encoding.UTF8, "application/json")
            })));
        using var oauth = new FikenOAuthService(http, store);

        var ex = await Assert.ThrowsAsync<FikenApiException>(() => oauth.GetValidAccessTokenAsync(ct: TestContext.Current.CancellationToken));

        Assert.Contains("beholdt", ex.Message);
        var loaded = await store.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ref-1", loaded.FikenOAuthRefreshToken);
    }

    [Fact]
    public async Task CancellationAfterTokenRequestIsSent_StillSavesRotatedTokens()
    {
        var store = await SeedAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            cts.Cancel(); // Kallet avbrytes mens Fiken allerede har rotert tokenet.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"acc-2","refresh_token":"ref-2","token_type":"bearer","expires_in":3600}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }));
        using var oauth = new FikenOAuthService(http, store);

        try
        {
            await oauth.GetValidAccessTokenAsync(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Akseptabelt, så lenge tokenene er lagret.
        }

        var loaded = await store.LoadSettingsAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ref-2", loaded.FikenOAuthRefreshToken);
        Assert.Equal("acc-2", loaded.FikenOAuthAccessToken);
    }

    [Fact]
    public async Task ForceRefresh_WithRejectedTokenAlreadyReplaced_DoesNotRefreshAgain()
    {
        var store = await SeedAsync(DateTimeOffset.UtcNow.AddHours(1));
        var calls = 0;
        using var http = new HttpClient(new StubHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }));
        using var oauth = new FikenOAuthService(http, store);

        // En annen prosess har allerede byttet acc-0 (som Fiken avviste) ut med acc-1.
        var token = await oauth.GetValidAccessTokenAsync(forceRefresh: true, rejectedAccessToken: "acc-0", ct: TestContext.Current.CancellationToken);

        Assert.Equal("acc-1", token);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RateLimitHandler_RecordsSpacingWhenRequestThrows()
    {
        var spacing = TimeSpan.FromMilliseconds(150);
        using var limiter = new FikenRateLimiter(spacing, Path.Combine(_tempDir, FikenRateLimiter.LockFileName));
        var times = new List<DateTimeOffset>();
        var first = true;
        using var http = new HttpClient(new FikenRateLimitHandler(limiter)
        {
            InnerHandler = new StubHttpHandler((_, _) =>
            {
                times.Add(DateTimeOffset.UtcNow);
                if (first)
                {
                    first = false;
                    throw new HttpRequestException("nettverksfeil");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })
        })
        {
            BaseAddress = new Uri(FikenClient.DefaultBaseAddress)
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("companies", TestContext.Current.CancellationToken));
        await http.GetAsync("companies", TestContext.Current.CancellationToken);

        Assert.True(times[1] - times[0] >= spacing - TimeSpan.FromMilliseconds(15));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
