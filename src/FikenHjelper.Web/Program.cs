using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.MinSpeiding;
using FikenHjelper.Core.QualityCheck;
using FikenHjelper.Web.Auth;
using FikenHjelper.Web.Components;
using FikenHjelper.Web.Components.Shared;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor;
using MudBlazor.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;

// Appen er norsk: tall, datoer og sortering formateres alltid som nb-NO, uavhengig av nettleserens språk.
var norwegian = CultureInfo.GetCultureInfo("nb-NO");
CultureInfo.DefaultThreadCurrentCulture = norwegian;
CultureInfo.DefaultThreadCurrentUICulture = norwegian;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddTransient<MudLocalizer, NorwegianMudLocalizer>();

var dataDirOverride = builder.Configuration["FikenHjelper:DataDirectory"];
if (!string.IsNullOrWhiteSpace(dataDirOverride))
{
    var dpKeysDir = Path.Combine(dataDirOverride, "dp-keys");
    Directory.CreateDirectory(dpKeysDir);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir));
}

builder.Services.AddSingleton<ILocalConfigStore>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var defaults = new AppSettingsDefaults(
        FikenApiToken: config["Fiken:ApiToken"],
        FikenCompanySlug: config["Fiken:CompanySlug"],
        MinSpeidingBaseUrl: config["MinSpeiding:BaseUrl"],
        FikenOAuthClientId: config["Fiken:ClientId"],
        FikenOAuthClientSecret: config["Fiken:ClientSecret"]);
    return new LocalConfigStore(defaults, dataDirOverride);
});

builder.Services.AddSingleton<IBudgetStore>(sp =>
    new JsonBudgetStore(sp.GetRequiredService<ILocalConfigStore>().DataDirectory));
builder.Services.AddSingleton<FikenDataCache>();
builder.Services.AddScoped<BudgetReportService>();

builder.Services.AddSingleton<FikenOAuthStateStore>();
builder.Services.AddHttpClient(FikenOAuthService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IFikenOAuthService>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return new FikenOAuthService(
        () => factory.CreateClient(FikenOAuthService.HttpClientName),
        sp.GetRequiredService<ILocalConfigStore>());
});

// Delt med MCP-serveren via låsfil i datamappen. Må registreres før AddFikenApiClient (TryAdd).
builder.Services.AddSingleton(sp => FikenRateLimiter.CreateShared(sp.GetRequiredService<ILocalConfigStore>().DataDirectory));
builder.Services.AddFikenApiClient();

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddTransient<QualityCheckService>();

builder.Services.AddHttpClient<IMinSpeidingClient, MinSpeidingClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseRequestLocalization(options =>
{
    options.SetDefaultCulture("nb-NO")
        .AddSupportedCultures("nb-NO")
        .AddSupportedUICultures("nb-NO");
    // Bare nb-NO: ikke la Accept-Language eller cookie bytte språk.
    options.RequestCultureProviders.Clear();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapGet("/fiken/oauth/login", async (
    HttpContext httpContext,
    ILocalConfigStore configStore,
    FikenOAuthStateStore stateStore,
    IFikenOAuthService oauthService,
    CancellationToken ct) =>
{
    var settings = await configStore.LoadSettingsAsync(ct);
    if (string.IsNullOrWhiteSpace(settings.FikenOAuthClientId))
    {
        return Results.Redirect("/innstillinger?oauthError=missing_client_id");
    }

    var redirectUri = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/fiken/oauth/callback";
    var codeVerifier = FikenPkceHelper.CreateRandomBase64Url(48);
    var codeChallenge = FikenPkceHelper.ComputeS256Challenge(codeVerifier);
    var state = stateStore.CreateState(codeVerifier, redirectUri);

    var authorizeUrl = oauthService.BuildAuthorizationUrl(
        settings.FikenOAuthClientId,
        redirectUri,
        state,
        codeChallenge);

    return Results.Redirect(authorizeUrl);
});

app.MapGet("/fiken/oauth/callback", async (
    string? code,
    string? state,
    string? error,
    FikenOAuthStateStore stateStore,
    IFikenOAuthService oauthService,
    FikenDataCache fikenCache,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        var safeError = error.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
            ? "access_denied"
            : "oauth_error";
        return Results.Redirect($"/innstillinger?oauthError={safeError}");
    }

    if (string.IsNullOrWhiteSpace(code)
        || string.IsNullOrWhiteSpace(state)
        || !stateStore.TryConsume(state, out var pending)
        || pending is null)
    {
        return Results.Redirect("/innstillinger?oauthError=invalid_state");
    }

    try
    {
        await oauthService.ExchangeAuthorizationCodeAsync(
            code,
            pending.CodeVerifier,
            pending.RedirectUri,
            state,
            ct);
        fikenCache.Clear();
        return Results.Redirect("/innstillinger?oauth=connected");
    }
    catch (FikenApiException ex)
    {
        logger.LogWarning(ex, "Fiken OAuth token exchange failed");
        return Results.Redirect("/innstillinger?oauthError=token_exchange_failed");
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error during Fiken OAuth callback");
        return Results.Redirect("/innstillinger?oauthError=token_exchange_failed");
    }
});

app.MapGet("/budsjett/eksport", async (
    int? year,
    int? years,
    BudgetReportService reports,
    ILogger<BudgetReportService> logger,
    CancellationToken ct) =>
{
    var lastYear = year ?? DateTime.Today.Year;
    if (lastYear is < BudgetData.MinYear or > BudgetData.MaxYear)
    {
        return Results.BadRequest($"Året må være mellom {BudgetData.MinYear} og {BudgetData.MaxYear}.");
    }

    var count = Math.Clamp(years ?? 3, 1, Math.Min(10, lastYear - BudgetData.MinYear + 1));
    BudgetComparison comparison;
    try
    {
        comparison = await reports.BuildComparisonAsync(
            Enumerable.Range(0, count).Select(i => lastYear - i),
            refresh: false,
            ct);
    }
    catch (FikenApiException ex)
    {
        return Results.Problem(ex.UserMessage, statusCode: StatusCodes.Status502BadGateway, title: "Klarte ikke å hente data fra Fiken");
    }
    catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
    {
        logger.LogWarning(ex, "Fiken svarte ikke ved eksport av budsjett");
        return Results.Problem("Fiken svarte ikke. Prøv igjen om litt.", statusCode: StatusCodes.Status502BadGateway, title: "Klarte ikke å hente data fra Fiken");
    }

    var bytes = BudgetWorkbookExporter.Export(comparison);
    var firstYear = lastYear - count + 1;
    var period = firstYear == lastYear ? $"{lastYear}" : $"{firstYear}-{lastYear}";
    return Results.File(
        bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"Budsjett og regnskap {period}.xlsx");
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
