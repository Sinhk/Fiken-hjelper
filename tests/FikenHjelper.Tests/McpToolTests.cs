using System.Text.Json;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Mcp;
using FikenHjelper.Mcp.Safety;
using FikenHjelper.Mcp.Services;
using FikenHjelper.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FikenHjelper.Tests;

public sealed class McpPurchaseFlowTests : IDisposable
{
    private readonly FakeFikenApi _api = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));

    private PurchaseTools CreateTools(
        bool testCompany = true,
        string? allowList = null,
        PendingDraftStore? pending = null,
        bool allowWithoutElicitation = false,
        bool inboxAlreadyUsed = false)
    {
        _api.Get("companies", $$"""[{"name":"Krets AS","slug":"test-as","hasApiAccess":true,"testCompany":{{(testCompany ? "true" : "false")}}}]""")
            .Get("companies/test-as/contacts/42", """{"contactId":42,"name":"Rema","supplier":true,"customer":false,"inactive":false}""")
            .Get("companies/test-as/purchases", "[]")
            .Get("companies/test-as/accounts", """[{"code":"6800","name":"Kontorrekvisita"}]""")
            .Get("companies/test-as/projects", """[{"projectId":7,"number":"7","name":"Landsleir 2026","completed":false}]""")
            .Get("companies/test-as/purchases/drafts", "[]")
            .Get("companies/test-as/inbox/11", $$"""{"documentId":11,"name":"Faktura Rema","filename":"rema.pdf","status":{{(inboxAlreadyUsed ? "true" : "false")}}}""")
            .On(HttpMethod.Post, "companies/test-as/purchases/drafts", (_, _) =>
                FakeFikenApi.Created("https://api.fiken.no/api/v2/companies/test-as/purchases/drafts/99"))
            .On(HttpMethod.Post, "companies/test-as/purchases/drafts/99/attachments", (_, _) =>
                FakeFikenApi.Created("https://api.fiken.no/api/v2/companies/test-as/purchases/drafts/99/attachments/x"));

        var store = _api.CreateStore();
        var client = _api.CreateClient(store);
        var cache = new FikenDataCache(_time);
        var context = new FikenContextService(store, client, cache);
        var settings = new McpSettings { AllowDraftsForCompany = allowList, AllowDraftsWithoutElicitation = allowWithoutElicitation };
        return new PurchaseTools(
            context,
            new FikenLookups(client, client, cache),
            client,
            new JsonBudgetStore(_api.DataDirectory),
            new WriteGuard(settings, context),
            pending ?? new PendingDraftStore(_time),
            settings,
            _time);
    }

    private static JsonElement ToJson(object value) =>
        JsonSerializer.SerializeToElement(value, ToolSupport.JsonOptions);

    private static PurchaseLineArgument[] Lines() =>
    [
        new() { Text = "Mat", Account = "6800", AmountKr = 1000m, ProjectId = 7 },
        new() { Text = "Diverse", Account = "6800", AmountKr = 249.5m }
    ];

    [Fact]
    public async Task PreviewThenCreate_CreatesExactlyThePreviewedDraftAndAttachesInboxDocument()
    {
        var tools = CreateTools();

        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42,
            invoiceNumber: "F-1", issueDate: "2026-11-20", dueDate: "2026-12-04"));

        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.True(preview.GetProperty("writeAllowed").GetBoolean());
        Assert.Equal(1249.5m, preview.GetProperty("totalKr").GetDecimal());
        var token = preview.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);

        var created = ToJson(await tools.CreatePurchaseDraft(token!));
        Assert.Equal(99, created.GetProperty("draftId").GetInt64());

        var posts = _api.Calls.Where(c => c.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Equal("/api/v2/companies/test-as/purchases/drafts", posts[0].PathAndQuery);
        using var body = JsonDocument.Parse(posts[0].Body!);
        Assert.False(body.RootElement.GetProperty("paid").GetBoolean());
        Assert.False(body.RootElement.GetProperty("cash").GetBoolean());
        Assert.Equal(100000, body.RootElement.GetProperty("lines")[0].GetProperty("gross").GetInt64());
        Assert.Equal(7, body.RootElement.GetProperty("lines")[0].GetProperty("projectId").GetInt64());
        Assert.Equal("/api/v2/companies/test-as/purchases/drafts/99/attachments?inboxDocumentId=11", posts[1].PathAndQuery);

        // Tokenet kan bare brukes én gang.
        await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!));
        Assert.Equal(2, _api.Calls.Count(c => c.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task RealCompanyWithoutAllowList_IsReadOnly()
    {
        var tools = CreateTools(testCompany: false);

        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20"));
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.False(preview.GetProperty("writeAllowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.TryGetProperty("confirmationToken", out var t) ? t.ValueKind : JsonValueKind.Null);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft("whatever"));
        Assert.Contains("Mcp__AllowDraftsForCompany", ex.Message);
        await Assert.ThrowsAsync<McpException>(() => tools.AttachDocumentToPurchaseDraft(99, inboxDocumentId: 11));
        Assert.DoesNotContain(_api.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task RealCompanyOnAllowList_RequiresElicitationUnlessExplicitlyWaived()
    {
        var tools = CreateTools(testCompany: false, allowList: "annet-as, TEST-AS");
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20"));
        Assert.True(preview.GetProperty("writeAllowed").GetBoolean());
        var token = preview.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // Uten bekreftelsesdialog (server = null betyr ingen elicitation) avvises opprettelsen i ekte foretak.
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!));
        Assert.Contains("elicitation", ex.Message);
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RealCompanyOnAllowList_WithElicitationWaived_CanCreate()
    {
        var tools = CreateTools(testCompany: false, allowList: "test-as", allowWithoutElicitation: true);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20"));

        var created = ToJson(await tools.CreatePurchaseDraft(preview.GetProperty("confirmationToken").GetString()!));
        Assert.Equal(99, created.GetProperty("draftId").GetInt64());
    }

    [Fact]
    public async Task DocumentUsedSincePreview_AbortsBeforeCreating()
    {
        var tools = CreateTools(inboxAlreadyUsed: true);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42, issueDate: "2026-11-20"));
        Assert.Contains(preview.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("allerede brukt"));

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(preview.GetProperty("confirmationToken").GetString()!));
        Assert.Contains("brukt som bilag", ex.Message);
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task InvalidPreview_GivesErrorsAndNoToken()
    {
        var tools = CreateTools();
        var preview = ToJson(await tools.PreviewPurchaseDraft(
            [new PurchaseLineArgument { Text = "x", Account = "9999", AmountKr = 10.001m }],
            supplierContactId: 42,
            issueDate: "2026-11-20"));

        Assert.False(preview.GetProperty("valid").GetBoolean());
        Assert.True(preview.GetProperty("errors").GetArrayLength() >= 2);
        Assert.False(preview.TryGetProperty("confirmationToken", out _));
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var pending = new PendingDraftStore(_time);
        var tools = CreateTools(pending: pending);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20"));
        var token = preview.GetProperty("confirmationToken").GetString();

        _time.Now += PendingDraftStore.Lifetime + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!));
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);
    }

    public void Dispose() => _api.Dispose();
}

public sealed class McpSafetyTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "krets", true)]
    [InlineData(false, "annet, KRETS", true)]
    [InlineData(false, "annet", false)]
    public void WriteGuard_AllowsTestCompaniesAndExplicitAllowList(bool isTest, string? allowList, bool expected)
    {
        var settings = new McpSettings { AllowDraftsForCompany = allowList };
        var decision = WriteGuard.Decide(new ActiveCompany("krets", "Kretsen", isTest, Found: true), settings.AllowedDraftCompanies);
        Assert.Equal(expected, decision.Allowed);
    }

    [Fact]
    public void PendingDraftStore_TokensAreUniqueAndSingleUse()
    {
        var store = new PendingDraftStore(TimeProvider.System);
        var request = new FikenPurchaseDraftRequest(null, null, null, null, "NOK", null, []);
        var (a, _) = store.Add("x", request, null, "s");
        var (b, _) = store.Add("x", request, null, "s");

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 20);
        Assert.True(store.TryTake(a, out var draft));
        Assert.Equal("x", draft!.CompanySlug);
        Assert.False(store.TryTake(a, out _));
        Assert.False(store.TryTake("ukjent", out _));
        Assert.False(store.TryTake(null, out _));
    }
}

/// <summary>Starter den bygde MCP-serveren over stdio, slik Claude Desktop gjør, med en tom datamappe.</summary>
public sealed class McpStdioEndToEndTests : IDisposable
{
    private static readonly string[] WriteTools = ["create_purchase_draft", "attach_document_to_purchase_draft"];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "fh-mcp-e2e-" + Guid.NewGuid().ToString("N"));

    private async Task<McpClient> StartAsync()
    {
        var serverDll = typeof(McpInstructions).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "fiken-hjelper",
            Command = "dotnet",
            Arguments = [serverDll],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["FikenHjelper__DataDirectory"] = _dataDir,
                ["DOTNET_ENVIRONMENT"] = "Production",
                // Overstyr eventuelle user-secrets på maskinen, så testen aldri kaller ekte Fiken.
                ["Fiken__ApiToken"] = "",
                ["Fiken__ClientId"] = "",
                ["Fiken__CompanySlug"] = "",
                ["Mcp__CompanySlug"] = ""
            }
        });
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ListsToolsWithCorrectAnnotations_PromptsAndInstructions()
    {
        await using var client = await StartAsync();

        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
                 {
                     "get_fiken_context", "list_inbox_documents", "list_ehf_documents", "get_inbox_document",
                     "suggest_accounting_and_projects", "preview_purchase_draft", "create_purchase_draft",
                     "get_budget_status", "explain_project_variance", "find_accounting_anomalies"
                 })
        {
            Assert.Contains(expected, names);
        }

        foreach (var tool in tools)
        {
            var annotations = tool.ProtocolTool.Annotations;
            Assert.NotNull(annotations);
            if (WriteTools.Contains(tool.Name))
            {
                Assert.False(annotations.ReadOnlyHint);
                Assert.False(annotations.DestructiveHint);
            }
            else
            {
                Assert.True(annotations.ReadOnlyHint, $"{tool.Name} skal være kun lesing");
            }
        }

        var prompts = await client.ListPromptsAsync();
        Assert.Contains(prompts, p => p.Name == "behandle_innboks");
        Assert.Contains("KJØPSUTKAST", client.ServerInstructions);
    }

    [Fact]
    public async Task WithoutFikenLogin_ToolsReturnClearErrorInsteadOfCrashing()
    {
        await using var client = await StartAsync();

        var result = await client.CallToolAsync("get_fiken_context");

        Assert.True(result.IsError);
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("Ikke koblet til Fiken", text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
