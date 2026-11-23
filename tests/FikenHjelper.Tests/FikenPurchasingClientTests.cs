using System.Net;
using System.Text.Json;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class FikenPurchasingClientTests
{
    [Fact]
    public async Task CreatePurchaseDraftAsync_SendsUnpaidNonCashDraftWithExpenseAccountAndProjectPerLine()
    {
        using var api = new FakeFikenApi();
        api.On(HttpMethod.Post, "companies/test-as/purchases/drafts", (_, _) =>
            FakeFikenApi.Created("https://api.fiken.no/api/v2/companies/test-as/purchases/drafts/555"));
        var client = api.CreateClient();

        var result = await client.CreatePurchaseDraftAsync("test-as", new FikenPurchaseDraftRequest(
            "2026-11-01", "2026-11-15", "F-100", 42, "NOK", "1234567",
            [
                new FikenPurchaseDraftLineRequest("Mat", "NONE", "6800", 12550, 12550, 7),
                new FikenPurchaseDraftLineRequest("Telt", "NONE", "6540", 1000, 1000, null)
            ]));

        Assert.Equal(555, result.Id);
        var call = Assert.Single(api.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        using var json = JsonDocument.Parse(call.Body!);
        var root = json.RootElement;
        Assert.False(root.GetProperty("cash").GetBoolean());
        Assert.False(root.GetProperty("paid").GetBoolean());
        Assert.False(root.TryGetProperty("payments", out _));
        Assert.Equal(42, root.GetProperty("contactId").GetInt64());
        Assert.Equal("F-100", root.GetProperty("invoiceNumber").GetString());
        var first = root.GetProperty("lines")[0];
        Assert.Equal("6800", first.GetProperty("incomeAccount").GetString());
        Assert.Equal(12550, first.GetProperty("net").GetInt64());
        Assert.Equal(12550, first.GetProperty("gross").GetInt64());
        Assert.Equal(7, first.GetProperty("projectId").GetInt64());
        Assert.False(root.GetProperty("lines")[1].TryGetProperty("projectId", out _));
    }

    [Theory]
    [InlineData(11L, null, "inboxDocumentId=11")]
    [InlineData(null, 22L, "ehfDocumentId=22")]
    public async Task AttachDocumentToPurchaseDraftAsync_UsesQueryParameterForExistingDocument(long? inboxId, long? ehfId, string expectedQuery)
    {
        using var api = new FakeFikenApi();
        api.On(HttpMethod.Post, "companies/test-as/purchases/drafts/555/attachments", (_, _) =>
            FakeFikenApi.Created("https://api.fiken.no/api/v2/companies/test-as/purchases/drafts/555/attachments/abc"));
        var client = api.CreateClient();

        await client.AttachDocumentToPurchaseDraftAsync("test-as", 555, new PurchaseDocumentSource(inboxId, ehfId));

        var call = Assert.Single(api.Calls);
        Assert.Equal($"/api/v2/companies/test-as/purchases/drafts/555/attachments?{expectedQuery}", call.PathAndQuery);
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task AttachDocumentToPurchaseDraftAsync_RejectsAmbiguousSource()
    {
        using var api = new FakeFikenApi();
        var client = api.CreateClient();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.AttachDocumentToPurchaseDraftAsync("test-as", 1, new PurchaseDocumentSource(1, 2)));
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task ReadEndpoints_UseExpectedPathsAndFilters()
    {
        using var api = new FakeFikenApi();
        api.Get("companies/test-as/inbox", "[]")
            .Get("companies/test-as/ehf", "[]")
            .Get("companies/test-as/purchases", "[]")
            .Get("companies/test-as/sales", "[]")
            .Get("companies/test-as/accounts", "[]")
            .Get("companies/test-as/contacts", "[]");
        var client = api.CreateClient();

        await client.GetInboxDocumentsAsync("test-as", "unused", "faktura");
        await client.GetEhfDocumentsAsync("test-as", "unprocessed", new DateOnly(2026, 1, 1));
        await client.GetPurchasesAsync("test-as", contactId: 42, dateFrom: new DateOnly(2024, 1, 1), paid: false);
        await client.GetSalesAsync("test-as", settled: false);
        await client.GetAccountsAsync("test-as", "4000-7999");
        await client.GetSuppliersAsync("test-as", organizationNumber: "987654321");

        var urls = api.Calls.Select(c => Uri.UnescapeDataString(c.PathAndQuery)).ToList();
        Assert.Contains(urls, u => u.StartsWith("/api/v2/companies/test-as/inbox?status=unused&sortBy=createdDate desc&name=faktura&page=0"));
        Assert.Contains(urls, u => u.Contains("/ehf?sortBy=createdDate desc&status=unprocessed&issueDateGe=2026-01-01"));
        Assert.Contains(urls, u => u.Contains("/purchases?sortBy=date desc&contactId=42&dateGe=2024-01-01&paid=false"));
        Assert.Contains(urls, u => u.Contains("/sales?settled=false"));
        Assert.Contains(urls, u => u.Contains("/accounts?range=4000-7999"));
        Assert.Contains(urls, u => u.Contains("/contacts?supplier=true&inactive=false&organizationNumber=987654321"));
        Assert.All(api.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task GetPurchasesAsync_ComputesGrossFromLines()
    {
        using var api = new FakeFikenApi();
        api.Get("companies/test-as/purchases", """
            [{"purchaseId":1,"date":"2026-03-01","kind":"supplier","paid":false,"currency":"NOK",
              "lines":[{"description":"a","netPrice":10000,"vat":2500,"account":"6800","vatType":"HIGH"},
                       {"description":"b","netPrice":500,"vat":0,"account":"6800","vatType":"NONE","projectId":7}]}]
            """);
        var purchases = await api.CreateClient().GetPurchasesAsync("test-as");

        var purchase = Assert.Single(purchases);
        Assert.Equal(13000, purchase.GrossAmount);
        Assert.Equal(7, purchase.Lines![1].ProjectId);
    }

    [Theory]
    [InlineData("https://fiken.no/api/v2/files/abc/faktura.pdf", true)]
    [InlineData("https://api.fiken.no/api/v2/files/abc", true)]
    [InlineData("http://fiken.no/files/abc", false)]
    [InlineData("https://fiken.no.evil.example/files/abc", false)]
    [InlineData("https://evilfiken.no/files/abc", false)]
    [InlineData("https://user:pw@fiken.no/files/abc", false)]
    [InlineData("/files/abc", false)]
    [InlineData(null, false)]
    public void IsTrustedFikenUrl_OnlyAllowsHttpsFikenHosts(string? url, bool expected)
    {
        Assert.Equal(expected, FikenClient.IsTrustedFikenUrl(url, out _));
    }

    [Fact]
    public async Task DownloadDocumentAsync_NeverSendsTokenToOtherHosts()
    {
        using var api = new FakeFikenApi();
        var client = api.CreateClient();

        var ex = await Assert.ThrowsAsync<FikenApiException>(() =>
            client.DownloadDocumentAsync("https://example.com/steal", 1024));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task DownloadDocumentAsync_RejectsFilesOverLimit()
    {
        using var api = new FakeFikenApi();
        api.On(HttpMethod.Get, "files/big.pdf", (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2048])
        });
        var client = api.CreateClient();

        var ex = await Assert.ThrowsAsync<FikenApiException>(() =>
            client.DownloadDocumentAsync("https://api.fiken.no/api/v2/files/big.pdf", 1024));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.StatusCode);
    }
}
