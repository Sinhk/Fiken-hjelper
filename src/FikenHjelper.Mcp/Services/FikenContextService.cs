using System.Net;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Mcp.Services;

public sealed record ActiveCompany(string Slug, string? Name, bool IsTestCompany, bool Found);

/// <summary>Finner aktivt foretak og innloggingsstatus. Foretakslisten mellomlagres.</summary>
public sealed class FikenContextService(
    ILocalConfigStore configStore,
    IFikenClient fiken,
    FikenDataCache cache)
{
    public async Task<string> GetCompanySlugAsync(CancellationToken ct)
    {
        var settings = await configStore.LoadSettingsAsync(ct);
        if (!settings.IsFikenConfigured)
        {
            throw new FikenApiException(
                HttpStatusCode.Unauthorized,
                "Ikke koblet til Fiken. Logg inn via Fiken-hjelper Web → Innstillinger på denne maskinen.");
        }

        if (string.IsNullOrWhiteSpace(settings.FikenCompanySlug))
        {
            throw new FikenApiException(
                HttpStatusCode.BadRequest,
                "Ingen Fiken-foretak valgt. Velg foretak i Fiken-hjelper Web → Innstillinger, eller sett Mcp__CompanySlug.");
        }

        return settings.FikenCompanySlug;
    }

    public async Task<ActiveCompany> GetActiveCompanyAsync(CancellationToken ct)
    {
        var slug = await GetCompanySlugAsync(ct);
        var companies = await cache.GetOrFetchAsync("mcp:companies", refresh: false, () => fiken.GetCompaniesAsync(ct: ct), ct);
        var company = companies.FirstOrDefault(c => string.Equals(c.Slug, slug, StringComparison.OrdinalIgnoreCase));
        return company is null
            ? new ActiveCompany(slug, null, IsTestCompany: false, Found: false)
            : new ActiveCompany(company.Slug, company.Name, company.TestCompany, Found: true);
    }
}
