using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Grants;
using FikenHjelper.Core.Purchasing;
using FikenHjelper.Mcp.Safety;
using FikenHjelper.Mcp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FikenHjelper.Mcp;

/// <summary>Innstillinger som bare gjelder MCP-serveren (miljøvariabler i MCP-klientens konfigurasjon).</summary>
public sealed class McpSettings
{
    /// <summary>Overstyrer foretak for denne prosessen (f.eks. testforetak). Lagres ikke i config.json.</summary>
    public string? CompanySlug { get; set; }

    /// <summary>Kommaseparert liste med foretak (slug) der kjøpsutkast kan opprettes selv om det ikke er testforetak.</summary>
    public string? AllowDraftsForCompany { get; set; }

    /// <summary>
    /// Tillat kjøpsutkast i ekte foretak når klienten ikke støtter bekreftelsesdialog (elicitation). Da er klientens
    /// godkjenning av verktøykall eneste vern mot at innhold i bilag lurer modellen. Standard false.
    /// </summary>
    public bool AllowDraftsWithoutElicitation { get; set; }

    /// <summary>MVA-type på kjøpsutkastlinjer. Kretsen er ikke MVA-registrert, så bare NONE (netto = brutto) støttes.</summary>
    public string PurchaseVatType => PurchaseDraftValidator.DefaultVatType;

    public IReadOnlySet<string> AllowedDraftCompanies =>
        (AllowDraftsForCompany ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public static class McpServiceRegistration
{
    public static IServiceCollection AddFikenHjelperMcp(this IServiceCollection services, IConfiguration configuration)
    {
        var mcpSettings = new McpSettings();
        configuration.GetSection("Mcp").Bind(mcpSettings);
        services.AddSingleton(mcpSettings);
        services.AddSingleton(TimeProvider.System);

        var dataDirOverride = configuration["FikenHjelper:DataDirectory"];
        services.AddSingleton<ILocalConfigStore>(_ =>
        {
            var defaults = new AppSettingsDefaults(
                FikenApiToken: configuration["Fiken:ApiToken"],
                FikenCompanySlug: configuration["Fiken:CompanySlug"],
                MinSpeidingBaseUrl: configuration["MinSpeiding:BaseUrl"],
                FikenOAuthClientId: configuration["Fiken:ClientId"],
                FikenOAuthClientSecret: configuration["Fiken:ClientSecret"]);
            ILocalConfigStore store = new LocalConfigStore(defaults, dataDirOverride);
            return string.IsNullOrWhiteSpace(mcpSettings.CompanySlug)
                ? store
                : new CompanySlugOverrideConfigStore(store, mcpSettings.CompanySlug.Trim());
        });

        services.AddSingleton<IBudgetStore>(sp =>
            new JsonBudgetStore(sp.GetRequiredService<ILocalConfigStore>().DataDirectory));
        services.AddSingleton(sp => new FikenDataCache(sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<BudgetReportService>();

        // Samme grants.json og grants.lock som Web. MCP-serveren bare leser tilskudd.
        services.AddSingleton<IGrantStore>(sp =>
            new JsonGrantStore(sp.GetRequiredService<ILocalConfigStore>().DataDirectory));
        services.AddScoped<GrantService>();

        services.AddHttpClient(FikenOAuthService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IFikenOAuthService>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new FikenOAuthService(
                () => factory.CreateClient(FikenOAuthService.HttpClientName),
                sp.GetRequiredService<ILocalConfigStore>());
        });

        // Delt med Web via låsfil i datamappen: maks 1 samtidig kall og 260 ms mellomrom på tvers av prosesser.
        services.AddSingleton(sp => FikenRateLimiter.CreateShared(sp.GetRequiredService<ILocalConfigStore>().DataDirectory));
        services.AddTransient<FikenRateLimitHandler>();
        services.AddHttpClient<FikenClient>(client =>
        {
            client.BaseAddress = new Uri(FikenClient.DefaultBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler<FikenRateLimitHandler>();
        services.AddTransient<IFikenClient>(sp => sp.GetRequiredService<FikenClient>());
        services.AddTransient<IFikenPurchasingClient>(sp => sp.GetRequiredService<FikenClient>());

        services.AddScoped<FikenContextService>();
        services.AddScoped<WriteGuard>();
        services.AddSingleton<PendingDraftStore>();
        services.AddScoped<FikenLookups>();

        return services;
    }
}
