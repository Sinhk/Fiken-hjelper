using System.ComponentModel;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Safety;

public sealed record WriteDecision(bool Allowed, string Reason);

/// <summary>Skjema for bekreftelsesdialogen (MCP elicitation) som vises direkte til brukeren.</summary>
public sealed class ConfirmationForm
{
    [Description("Kryss av for å bekrefte")]
    public bool Bekreft { get; set; }
}

/// <summary>
/// Skrivevern:
/// 1. Kjøpsutkast kan bare opprettes i testforetak, eller i foretak brukeren eksplisitt har tillatt med
///    <c>Mcp__AllowDraftsForCompany</c>. Standard for kretsens ekte regnskap er derfor kun lesing.
/// 2. Hvis klienten støtter elicitation, må brukeren bekrefte i en dialog fra serveren (ikke via modellen),
///    så innhold i bilag ikke kan lure modellen til å opprette utkast. I ekte foretak kreves dette, med mindre
///    brukeren har valgt <c>Mcp__AllowDraftsWithoutElicitation=true</c> og stoler på klientens godkjenning av verktøykall.
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

    /// <summary>Sjekker skrivevernet og ber brukeren bekrefte <paramref name="message"/> direkte.</summary>
    public async Task EnsureAllowedAndConfirmedAsync(McpServer? server, string message, CancellationToken ct)
    {
        var company = await context.GetActiveCompanyAsync(ct);
        var decision = Decide(company, settings.AllowedDraftCompanies);
        if (!decision.Allowed)
        {
            throw new McpException(decision.Reason);
        }

        // Bare skjema-elicitation kan vise bekreftelsesdialogen. En klient med bare URL-elicitation behandles som uten.
        if (server?.ClientCapabilities?.Elicitation?.Form is not null)
        {
            var result = await server.ElicitAsync<ConfirmationForm>(message, cancellationToken: ct);
            if (!result.IsAccepted || result.Content?.Bekreft != true)
            {
                throw new McpException("Brukeren bekreftet ikke i dialogen. Ingenting er opprettet i Fiken.");
            }

            return;
        }

        if (company.IsTestCompany || settings.AllowDraftsWithoutElicitation)
        {
            return;
        }

        throw new McpException(
            "MCP-klienten støtter ikke bekreftelsesdialog (elicitation), og dette er ikke et testforetak. " +
            "Opprett utkastet i Fiken, bruk en klient med elicitation, eller la brukeren selv sette " +
            "Mcp__AllowDraftsWithoutElicitation=true for å stole på klientens godkjenning av verktøykall.");
    }
}
