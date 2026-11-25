using System.Net;
using System.Text;
using System.Text.Json;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class FikenClientTests
{
    [Fact]
    public async Task GetCustomersAsync_PaginatesZeroIndexedPagesAndSendsBearerToken()
    {
        var seenUrls = new List<string>();
        var handler = new StubHttpHandler((req, _) =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test-token-123", req.Headers.Authorization?.Parameter);
            seenUrls.Add(req.RequestUri!.PathAndQuery);

            var isPage0 = req.RequestUri.Query.Contains("page=0");
            var body = isPage0
                ? """[{"contactId":1,"name":"Kunde 1","customer":true,"supplier":false,"inactive":false}]"""
                : """[{"contactId":2,"name":"Kunde 2","customer":true,"supplier":false,"inactive":false}]""";

            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            res.Headers.Add("Fiken-Api-Page-Count", "2");
            return Task.FromResult(res);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
        var store = new LocalConfigStore(
            new AppSettingsDefaults(FikenApiToken: "test-token-123"),
            Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N")));

        var client = new FikenClient(http, store);
        var customers = await client.GetCustomersAsync("romsdal-krets", TestContext.Current.CancellationToken);

        Assert.Equal(2, customers.Count);
        Assert.Contains(seenUrls, u => u.Contains("page=0&pageSize=100"));
        Assert.Contains(seenUrls, u => u.Contains("page=1&pageSize=100"));
    }

    [Fact]
    public async Task CreateInvoiceDraftAsync_SendsExpectedJsonAndExtractsDraftIdFromLocation()
    {
        string? capturedJson = null;
        var handler = new StubHttpHandler(async (req, ct) =>
        {
            capturedJson = await req.Content!.ReadAsStringAsync(ct);
            var res = new HttpResponseMessage(HttpStatusCode.Created);
            res.Headers.Location = new Uri("https://api.fiken.no/api/v2/companies/romsdal-krets/invoices/drafts/987654");
            return res;
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
        var store = new LocalConfigStore(
            new AppSettingsDefaults(FikenApiToken: "test-token-123"),
            Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N")));

        var client = new FikenClient(http, store);
        var reqBody = new FikenInvoiceDraftRequest(
            Type: "invoice",
            IssueDate: "2026-11-23",
            DaysUntilDueDate: 14,
            InvoiceText: "Deltakeravgift",
            YourReference: null,
            OurReference: "Kasserer",
            OrderReference: "MS-1-molde",
            Lines:
            [
                new FikenInvoiceDraftLineRequest("Ola Nordmann", 45000, "OUTSIDE", 1m, "3200", "Kretsleir")
            ],
            Currency: "NOK",
            BankAccountNumber: "12345678903",
            CustomerId: 1001,
            ProjectId: 777);

        var result = await client.CreateInvoiceDraftAsync("romsdal-krets", reqBody, TestContext.Current.CancellationToken);

        Assert.Equal(987654, result.Id);
        Assert.NotNull(capturedJson);

        using var doc = JsonDocument.Parse(capturedJson);
        var root = doc.RootElement;
        Assert.Equal("invoice", root.GetProperty("type").GetString());
        Assert.Equal(1001, root.GetProperty("customerId").GetInt64());
        Assert.Equal(777, root.GetProperty("projectId").GetInt64());
        Assert.Equal("12345678903", root.GetProperty("bankAccountNumber").GetString());
        Assert.False(root.TryGetProperty("yourReference", out _));

        var firstLine = root.GetProperty("lines")[0];
        Assert.Equal(45000, firstLine.GetProperty("unitPrice").GetInt64());
        Assert.Equal("OUTSIDE", firstLine.GetProperty("vatType").GetString());
        Assert.Equal("3200", firstLine.GetProperty("incomeAccount").GetString());
    }

    [Fact]
    public async Task FikenRateLimitHandler_SerializesConcurrentRequestsAndRetries429()
    {
        var activeCount = 0;
        var maxObservedActive = 0;
        var callCount = 0;

        var inner = new StubHttpHandler(async (_, ct) =>
        {
            var current = Interlocked.Increment(ref activeCount);
            if (current > maxObservedActive)
            {
                maxObservedActive = current;
            }

            try
            {
                await Task.Delay(25, ct);
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                {
                    var retryRes = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    retryRes.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                    return retryRes;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeCount);
            }
        });

        using var rateHandler = new FikenRateLimitHandler(TimeSpan.FromMilliseconds(10)) { InnerHandler = inner };
        using var http = new HttpClient(rateHandler) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => http.GetAsync("companies"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, maxObservedActive);
        Assert.Equal(6, callCount); // 5 calls + 1 retry for initial 429
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public async Task SharedFikenRateLimiter_SerializesRequestsAcrossSeparateHandlerInstances()
    {
        var activeCount = 0;
        var maxObservedActive = 0;

        using var limiter = new FikenRateLimiter(TimeSpan.FromMilliseconds(5));
        using var handler1 = new FikenRateLimitHandler(limiter)
        {
            InnerHandler = new StubHttpHandler(async (_, ct) =>
            {
                var current = Interlocked.Increment(ref activeCount);
                maxObservedActive = Math.Max(maxObservedActive, current);
                try
                {
                    await Task.Delay(25, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            })
        };
        using var handler2 = new FikenRateLimitHandler(limiter)
        {
            InnerHandler = new StubHttpHandler(async (_, ct) =>
            {
                var current = Interlocked.Increment(ref activeCount);
                maxObservedActive = Math.Max(maxObservedActive, current);
                try
                {
                    await Task.Delay(25, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            })
        };

        using var http1 = new HttpClient(handler1) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
        using var http2 = new HttpClient(handler2) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };

        var tasks = new[]
        {
            http1.GetAsync("companies", TestContext.Current.CancellationToken),
            http2.GetAsync("companies", TestContext.Current.CancellationToken),
            http1.GetAsync("companies", TestContext.Current.CancellationToken),
            http2.GetAsync("companies", TestContext.Current.CancellationToken)
        };

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, maxObservedActive);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => responder(request, cancellationToken);
}
