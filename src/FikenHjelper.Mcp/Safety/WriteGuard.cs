using FikenHjelper.Mcp.Services;
using ModelContextProtocol;

namespace FikenHjelper.Mcp.Safety;

public sealed record WriteDecision(bool Allowed, string Reason);

/// <summary>
/// Skrivevern: kjøpsutkast kan bare opprettes i testforetak, eller i foretak som brukeren eksplisitt har tillatt
/// med <c>Mcp__AllowDraftsForCompany</c>. Standard for kretsens ekte regnskap er derfor kun lesing.
/// </summary>
public sealed class WriteGuard(McpSettings settings, FikenContextService context)
{
    public static WriteDecision Decide(ActiveCompany company, IReadOnlySet<string> allowList)
    {
        if (company.Found && company.IsTestCompany)
        {
            return new WriteDecision(true, "Testforetak: kjøpsutkast kan opprettes.");
        }

        if (allowList.Contains(company.Slug))
        {
            return new WriteDecision(true, $"Foretaket {company.Slug} er tillatt i Mcp__AllowDraftsForCompany: kjøpsutkast kan opprettes.");
        }

        return new WriteDecision(
            false,
            $"Kun lesing: «{company.Name ?? company.Slug}» er ikke et testforetak. For å tillate kjøpsutkast her må brukeren selv sette " +
            $"miljøvariabelen Mcp__AllowDraftsForCompany={company.Slug} i MCP-klientens konfigurasjon og starte serveren på nytt.");
    }

    public async Task<WriteDecision> CheckAsync(CancellationToken ct)
    {
        var company = await context.GetActiveCompanyAsync(ct);
        return Decide(company, settings.AllowedDraftCompanies);
    }

    public async Task EnsureAllowedAsync(CancellationToken ct)
    {
        var decision = await CheckAsync(ct);
        if (!decision.Allowed)
        {
            throw new McpException(decision.Reason);
        }
    }
}
