using System.IO.Pipelines;
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
using ModelContextProtocol.Server;

namespace FikenHjelper.Tests;

public sealed class McpPurchaseFlowTests : IDisposable
{
    private readonly FakeFikenApi _api = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    private bool _inboxUsed;

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
            .On(HttpMethod.Get, "companies/test-as/inbox/11", (_, _) => FakeFikenApi.Json(
                $$"""{"documentId":11,"name":"Faktura Rema","filename":"rema.pdf","status":{{(inboxAlreadyUsed || _inboxUsed ? "true" : "false")}}}"""))
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

    /// <summary>
    /// Kjører create_purchase_draft gjennom en ekte MCP-server i minnet, med en klient som svarer på
    /// bekreftelsesdialogen (elicitation) slik <paramref name="answer"/> sier.
    /// </summary>
    private async Task<(CallToolResult Result, string? DialogText)> CreateViaClientWithElicitationAsync(
        PurchaseTools tools,
        string token,
        Func<ElicitResult> answer)
    {
        var ct = TestContext.Current.CancellationToken;
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var options = new McpServerOptions
        {
            ToolCollection =
            [
                McpServerTool.Create(
                    (string confirmationToken, McpServer server, CancellationToken toolCt) => tools.CreatePurchaseDraft(confirmationToken, server, toolCt),
                    new McpServerToolCreateOptions { Name = "create_purchase_draft", SerializerOptions = ToolSupport.JsonOptions })
            ]
        };
        await using var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()), options);
        var serverRun = server.RunAsync(ct);

        string? dialogText = null;
        var clientOptions = new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    dialogText = request?.Message;
                    return ValueTask.FromResult(answer());
                }
            }
        };
        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), clientOptions, cancellationToken: ct))
        {
            var result = await client.CallToolAsync(
                "create_purchase_draft",
                new Dictionary<string, object?> { ["confirmationToken"] = token },
                cancellationToken: ct);
            return (result, dialogText);
        }
    }

    private static ElicitResult Answer(string action, bool? confirm = null) => new()
    {
        Action = action,
        Content = confirm is { } value
            ? new Dictionary<string, JsonElement> { ["bekreft"] = JsonSerializer.SerializeToElement(value) }
            : null
    };

    [Fact]
    public async Task Elicitation_Accepted_CreatesDraftAndDialogShowsLinesAccountsAndProjects()
    {
        var tools = CreateTools(testCompany: false, allowList: "test-as");
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42,
            invoiceNumber: "F-1", issueDate: "2026-11-20", dueDate: "2026-12-04", kid: "12345678", ct: TestContext.Current.CancellationToken));
        var token = preview.GetProperty("confirmationToken").GetString()!;

        var (result, dialog) = await CreateViaClientWithElicitationAsync(tools, token, () => Answer("accept", confirm: true));

        Assert.NotEqual(true, result.IsError);
        Assert.Contains(_api.Calls, c => c.Method == HttpMethod.Post && c.PathAndQuery.EndsWith("/purchases/drafts", StringComparison.Ordinal));
        Assert.NotNull(dialog);
        Assert.Contains("Mat: konto 6800 Kontorrekvisita, prosjekt 7 Landsleir 2026", dialog);
        Assert.Contains("Diverse: konto 6800 Kontorrekvisita, uten prosjekt", dialog);
        Assert.Contains("KID 12345678", dialog);
        Assert.Contains("Rema", dialog);
    }

    [Theory]
    [InlineData("accept", false)]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    public async Task Elicitation_NotConfirmed_CreatesNothingAndConsumesToken(string action, bool? confirm)
    {
        var tools = CreateTools(testCompany: false, allowList: "test-as");
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42,
            issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));
        var token = preview.GetProperty("confirmationToken").GetString()!;

        var (result, _) = await CreateViaClientWithElicitationAsync(tools, token, () => Answer(action, confirm));

        Assert.True(result.IsError);
        Assert.Contains("bekreftet ikke", Text(result));
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);

        // Samme token kan ikke brukes igjen: et avslag krever ny forhåndsvisning.
        var again = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token, ct: TestContext.Current.CancellationToken));
        Assert.Contains("confirmationToken", again.Message);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task PreviewThenCreate_CreatesExactlyThePreviewedDraftAndAttachesInboxDocument()
    {
        var tools = CreateTools();

        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42,
            invoiceNumber: "F-1", issueDate: "2026-11-20", dueDate: "2026-12-04", ct: TestContext.Current.CancellationToken));

        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.True(preview.GetProperty("writeAllowed").GetBoolean());
        Assert.Equal(1249.5m, preview.GetProperty("totalKr").GetDecimal());
        var token = preview.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);

        var created = ToJson(await tools.CreatePurchaseDraft(token!, ct: TestContext.Current.CancellationToken));
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
        await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!, ct: TestContext.Current.CancellationToken));
        Assert.Equal(2, _api.Calls.Count(c => c.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task RealCompanyWithoutAllowList_IsReadOnly()
    {
        var tools = CreateTools(testCompany: false);

        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.False(preview.GetProperty("writeAllowed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.TryGetProperty("confirmationToken", out var t) ? t.ValueKind : JsonValueKind.Null);

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft("whatever", ct: TestContext.Current.CancellationToken));
        Assert.Contains("Mcp__AllowDraftsForCompany", ex.Message);
        await Assert.ThrowsAsync<McpException>(() => tools.AttachDocumentToPurchaseDraft(99, inboxDocumentId: 11, ct: TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_api.Calls, c => c.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task RealCompanyOnAllowList_RequiresElicitationUnlessExplicitlyWaived()
    {
        var tools = CreateTools(testCompany: false, allowList: "annet-as, TEST-AS");
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));
        Assert.True(preview.GetProperty("writeAllowed").GetBoolean());
        var token = preview.GetProperty("confirmationToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        // Uten bekreftelsesdialog (server = null betyr ingen elicitation) avvises opprettelsen i ekte foretak.
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!, ct: TestContext.Current.CancellationToken));
        Assert.Contains("elicitation", ex.Message);
        Assert.DoesNotContain(_api.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RealCompanyOnAllowList_WithElicitationWaived_CanCreate()
    {
        var tools = CreateTools(testCompany: false, allowList: "test-as", allowWithoutElicitation: true);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));

        var created = ToJson(await tools.CreatePurchaseDraft(preview.GetProperty("confirmationToken").GetString()!, ct: TestContext.Current.CancellationToken));
        Assert.Equal(99, created.GetProperty("draftId").GetInt64());
    }

    [Fact]
    public async Task DocumentAlreadyUsed_IsAValidationErrorWithoutToken()
    {
        var tools = CreateTools(inboxAlreadyUsed: true);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));

        Assert.False(preview.GetProperty("valid").GetBoolean());
        Assert.Contains(preview.GetProperty("errors").EnumerateArray(), e => e.GetString()!.Contains("allerede brukt"));
        Assert.False(preview.TryGetProperty("confirmationToken", out _));
    }

    [Fact]
    public async Task DocumentUsedSincePreview_AbortsBeforeCreating()
    {
        var tools = CreateTools();
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), inboxDocumentId: 11, supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));
        Assert.True(preview.GetProperty("valid").GetBoolean());
        _inboxUsed = true; // f.eks. brukt i Fiken eller Web mellom forhåndsvisning og opprettelse

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(preview.GetProperty("confirmationToken").GetString()!, ct: TestContext.Current.CancellationToken));
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
            issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));

        Assert.False(preview.GetProperty("valid").GetBoolean());
        Assert.True(preview.GetProperty("errors").GetArrayLength() >= 2);
        Assert.False(preview.TryGetProperty("confirmationToken", out _));
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var pending = new PendingDraftStore(_time);
        var tools = CreateTools(pending: pending);
        var preview = ToJson(await tools.PreviewPurchaseDraft(Lines(), supplierContactId: 42, issueDate: "2026-11-20", ct: TestContext.Current.CancellationToken));
        var token = preview.GetProperty("confirmationToken").GetString();

        _time.Now += PendingDraftStore.Lifetime + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<McpException>(() => tools.CreatePurchaseDraft(token!, ct: TestContext.Current.CancellationToken));
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
        var serverDll = FindServerDll();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "fiken-hjelper",
            Command = DotnetHost(),
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

    /// <summary>
    /// Serveren må startes fra sin egen byggmappe. Testprosjektet bruker ASP.NET Core-rammeverket,
    /// så pakker som Microsoft.Extensions.Hosting kopieres ikke til testmappa.
    /// </summary>
    /// <summary>Samme dotnet som kjører testene, så serveren får riktig runtime selv om en annen dotnet ligger først i PATH.</summary>
    private static string DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        // Testverten kan være en egen kjørbar fil (testhost). Finn dotnet ut fra runtime-mappa:
        // {root}/shared/Microsoft.NETCore.App/{versjon}/
        var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar));
        var root = runtimeDir.Parent?.Parent?.Parent?.FullName;
        var candidate = root is null ? null : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return candidate is not null && File.Exists(candidate) ? candidate : "dotnet";
    }

    private static string FindServerDll()
    {
        // .../tests/FikenHjelper.Tests/bin/{Configuration}/{TargetFramework}/
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = testOutput.Name;
        var configuration = testOutput.Parent!.Name;
        var repoRoot = testOutput.Parent.Parent!.Parent!.Parent!.Parent!.FullName;
        var serverDll = Path.Combine(repoRoot, "src", "FikenHjelper.Mcp", "bin", configuration, framework, "FikenHjelper.Mcp.dll");
        Assert.True(File.Exists(serverDll), $"Fant ikke den bygde MCP-serveren: {serverDll}");
        return serverDll;
    }

    [Fact]
    public async Task ListsToolsWithCorrectAnnotations_PromptsAndInstructions()
    {
        await using var client = await StartAsync();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
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

        var prompts = await client.ListPromptsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(prompts, p => p.Name == "behandle_innboks");
        Assert.Contains("KJØPSUTKAST", client.ServerInstructions);
    }

    [Fact]
    public async Task WithoutFikenLogin_ToolsReturnClearErrorInsteadOfCrashing()
    {
        await using var client = await StartAsync();

        var result = await client.CallToolAsync("get_fiken_context", cancellationToken: TestContext.Current.CancellationToken);

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
