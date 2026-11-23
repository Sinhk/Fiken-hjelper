using System.ComponentModel;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Mcp.Safety;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Tools;

[McpServerToolType]
public sealed class ContextTools(
    FikenContextService context,
    FikenLookups lookups,
    IFikenPurchasingClient purchasing,
    ILocalConfigStore configStore,
    McpSettings mcpSettings)
{
    [McpServerTool(Name = "get_fiken_context", Title = "Fiken-foretak og status", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Viser hvilket Fiken-foretak serveren bruker, om det er et testforetak, om kjøpsutkast kan opprettes, og MVA-typen som brukes på kjøpsutkast. Kall dette først i en ny samtale.")]
    public Task<object> GetFikenContext(CancellationToken ct) => ToolSupport.Run<object>(async () =>
    {
        var company = await context.GetActiveCompanyAsync(ct);
        var decision = WriteGuard.Decide(company, mcpSettings.AllowedDraftCompanies);
        var settings = await configStore.LoadSettingsAsync(ct);
        return new
        {
            company = new { company.Slug, company.Name, company.IsTestCompany, foundInFiken = company.Found },
            auth = settings.HasFikenOAuthTokens ? "OAuth2" : "API-nøkkel",
            companyOverriddenForMcp = !string.IsNullOrWhiteSpace(mcpSettings.CompanySlug),
            purchaseDraftsAllowed = decision.Allowed,
            writePolicy = decision.Reason,
            purchaseVatType = mcpSettings.PurchaseVatType,
            notes = new[]
            {
                "Verktøyene oppretter aldri bokførte eller betalte kjøp, bare kjøpsutkast som må kontrolleres og bokføres i Fiken.",
                "Budsjett- og regnskapstall bruker fortegnsregelen fra Fase 3 (inntekt positiv, kostnad negativ), som ennå ikke er verifisert mot ekte data."
            }
        };
    });

    [McpServerTool(Name = "list_projects", Title = "Prosjekter i Fiken", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lister prosjekter i Fiken (id, nummer, navn, datoer). Som standard bare aktive prosjekter.")]
    public Task<object> ListProjects(
        [Description("Ta med avsluttede prosjekter.")] bool includeCompleted = false,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var slug = await context.GetCompanySlugAsync(ct);
        var projects = await lookups.GetProjectsAsync(slug, ct);
        return projects
            .Where(p => includeCompleted || !p.Completed)
            .OrderBy(p => p.Number, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new { p.ProjectId, p.Number, p.Name, p.StartDate, p.EndDate, p.Completed })
            .ToList();
    });

    [McpServerTool(Name = "list_accounts", Title = "Kontoplan", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Lister kontoer fra kontoplanen i Fiken. Standard er kostnadskontoer (4000-7999).")]
    public Task<object> ListAccounts(
        [Description("Kontointervall, f.eks. \"4000-7999\" eller \"3000-3999, 6800\".")] string range = FikenLookups.ExpenseRange,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var slug = await context.GetCompanySlugAsync(ct);
        var accounts = await lookups.GetAccountsAsync(slug, range, ct);
        return accounts.Select(a => new { a.Code, a.Name }).ToList();
    });

    [McpServerTool(Name = "find_suppliers", Title = "Finn leverandør", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Søker etter leverandører (kontakter merket som leverandør) på navn eller organisasjonsnummer.")]
    public Task<object> FindSuppliers(
        [Description("Del av navnet eller organisasjonsnummeret (9 siffer).")] string nameOrOrgNumber,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var slug = await context.GetCompanySlugAsync(ct);
        var query = (nameOrOrgNumber ?? string.Empty).Trim();
        if (query.Length < 2)
        {
            throw new ArgumentException("Oppgi minst to tegn.");
        }

        var digits = new string(query.Where(char.IsDigit).ToArray());
        var suppliers = digits.Length == 9
            ? await purchasing.GetSuppliersAsync(slug, organizationNumber: digits, ct: ct)
            : (await lookups.GetSuppliersAsync(slug, ct))
                .Where(s => s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        return suppliers
            .Take(25)
            .Select(s => new { s.ContactId, s.Name, s.OrganizationNumber, s.Email })
            .ToList();
    });
}
