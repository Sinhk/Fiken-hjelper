using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.MinSpeiding;
using FikenHjelper.Web.Auth;
using FikenHjelper.Web.Components;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

builder.Services.AddFikenApiClient();

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

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.Run();
