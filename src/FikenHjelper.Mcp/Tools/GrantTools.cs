using System.ComponentModel;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Grants;
using FikenHjelper.Core.QualityCheck;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Tools;

/// <summary>Lesing av søknader og tilskudd fra Fiken-hjelper. Ingen skriveverktøy: tilskudd endres bare i web-appen.</summary>
[McpServerToolType]
public sealed class GrantTools(
    GrantService grants,
    FikenContextService context,
    TimeProvider time)
{
    internal const string Note =
        "Beløp er i kroner. «Brukt» er brutto kostnader (konto 4000–7999 og 8100–8799) på koblede prosjekter i bruksperioden ganget med tilskuddets andel. " +
        "Mottatt er summen av utbetalinger (koblede Fiken-posteringer og manuelle linjer). Gjenstår = innvilget − mottatt. Ubrukt = mottatt − brukt (negativt betyr brukt før mottatt). " +
        "Felt som mangler (null/utelatt) er ukjent eller ikke registrert, ikke 0: se usedStatus, receivedStatus og notes.";

    [McpServerTool(Name = "get_grant_status", Title = "Status for søknader og tilskudd", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Henter status for søknader og tilskudd registrert i Fiken-hjelper: tilskuddsgiver, type, status, frister, søkt, innvilget, mottatt, brukt, gjenstår å motta, ubrukt, bruk per konto (de største) og kontrollfunn (frister, brukt mer enn innvilget, ikke mottatt etter 60 dager, kostnader utenfor bruksperioden, ugyldig andelsfordeling, utbetaling som ikke finnes i Fiken). Bare lesing. Mangler Fiken-data, står det i svaret i stedet for 0.")]
    public Task<object> GetGrantStatus(
        [Description("Bare tilskudd som hører til dette året (bruksperioden overlapper året, eller vedtaket er fattet i året).")] int? year = null,
        [Description("Bare tilskudd med denne statusen: Planlagt, Sendt, Innvilget, Delvis innvilget, Avslått, Trukket, Rapportert eller Avsluttet.")] string? status = null,
        [Description("Id-en til ett tilskudd (fra et tidligere svar eller lenken /tilskudd/{id}).")] string? grantId = null,
        [Description("Søk i navn, tilskuddsgiver og ordning (delstreng, uavhengig av store og små bokstaver).")] string? name = null,
        [Description("Antall kontoer i «bruk per konto» (0-50). Standard 10.")] int topAccounts = 10,
        [Description("Hent ferske tall fra Fiken i stedet for mellomlagrede (maks 15 min gamle).")] bool refresh = false,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        int? y = year is null ? null : ToolSupport.ValidateYear(year.Value);
        var statusFilter = ParseStatus(status);
        Guid? id = null;
        if (!string.IsNullOrWhiteSpace(grantId))
        {
            id = Guid.TryParse(grantId.Trim(), out var parsed)
                ? parsed
                : throw new McpException("grantId må være en gyldig id (GUID), f.eks. fra et tidligere svar.");
        }

        var top = Math.Clamp(topAccounts, 0, 50);
        var today = ToolSupport.Today(time);
        var all = (await grants.LoadAsync(ct)).Grants;

        var selected = GrantOverviewLogic.SelectGrants(all, y, statusFilter)
            .Where(g => id is null || g.Id == id)
            .Where(g => string.IsNullOrWhiteSpace(name) || Matches(g, name.Trim()))
            .OrderBy(g => g.Grantor, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var filterText = FilterText(GrantOverviewLogic.FilterText(y, statusFilter), id, name);
        if (selected.Count == 0)
        {
            // Ingen tilskudd i utvalget: ikke hent Fiken-data og ikke meld «mangler Fiken-data».
            return new
            {
                note = Note,
                today = today.ToString("yyyy-MM-dd"),
                count = 0,
                totalGrantsRegistered = all.Count,
                filter = filterText,
                grants = Array.Empty<object>()
            };
        }

        var (fiken, fikenProblem) = await LoadFikenAsync(selected, refresh, ct);

        var check = GrantCheckRules.Build(all, fiken, today, fikenProblem);
        var rows = selected.Select(g =>
        {
            var report = GrantReportBuilder.Build(g, all, fiken.Entries, fiken.Accounts, fiken.Projects);
            return Describe(g, report, check, today, top);
        }).ToList();

        return new
        {
            note = Note,
            today = today.ToString("yyyy-MM-dd"),
            count = rows.Count,
            totalGrantsRegistered = all.Count,
            filter = filterText,
            hasFikenData = fiken.Entries is not null,
            missingFikenData = check.MissingDataNote,
            grants = rows
        };
    });

    private async Task<(GrantFikenData Data, string? Problem)> LoadFikenAsync(IReadOnlyList<Grant> selected, bool refresh, CancellationToken ct)
    {
        try
        {
            await context.GetCompanySlugAsync(ct);
            return (await grants.LoadFikenAsync(grants.YearsFor(selected), refresh, ct), null);
        }
        catch (Exception ex) when (ex is FikenApiException or HttpRequestException or TimeoutException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Tilskuddene ligger lokalt og kan vises uten Fiken. Manglende tall merkes i svaret.
            var message = ex switch
            {
                HttpRequestException => "Fikk ikke kontakt med Fiken.",
                FikenApiException => ex.Message,
                _ => "Fiken svarte ikke i tide."
            };
            return (GrantFikenData.None, message);
        }
    }

    private static object Describe(Grant g, GrantReport report, GrantCheckSection check, DateOnly today, int top)
    {
        var received = GrantOverviewLogic.ReceivedOrNull(report);
        var remaining = GrantOverviewLogic.RemainingOrNull(report);
        var notes = new List<string>();

        string receivedStatus;
        if (received is not null)
        {
            receivedStatus = "ok";
        }
        else if (!report.HasFikenData)
        {
            receivedStatus = "missingFikenData";
            notes.Add("Mottatt: mangler Fiken-data (utbetalinger er koblet til Fiken-posteringer som ikke kunne hentes).");
        }
        else
        {
            receivedStatus = "paymentNotFound";
            notes.Add("Mottatt: en koblet utbetaling finnes ikke i hentede posteringer, så summen er ikke kjent.");
        }

        string usedStatus;
        if (report.Used is not null)
        {
            usedStatus = "ok";
        }
        else if (report.UsedNeedsFikenData)
        {
            usedStatus = "missingFikenData";
            notes.Add("Brukt: mangler Fiken-data (prosjekter eller kontoer er koblet, men posteringene kunne ikke hentes).");
        }
        else
        {
            usedStatus = "notRegistered";
            notes.Add("Brukt: ikke registrert (ingen prosjekter, kontoer eller manuell bruk er lagt inn).");
        }

        if (g.GrantedAmount is null)
        {
            notes.Add("Innvilget beløp er ikke registrert.");
        }

        if (g.Payments.Count == 0)
        {
            notes.Add("Ingen utbetalinger er registrert.");
        }

        var deadlines = GrantOverviewLogic.Deadlines(g, today).Select(d => new
        {
            kind = d.Label,
            date = d.Date.ToString("yyyy-MM-dd"),
            state = d.State == GrantDeadlineState.Overdue ? "passert" : "innen 30 dager",
            daysLeft = d.Days
        }).ToList();

        var findings = check.Findings.Where(f => f.GrantId == g.Id).Select(f => new
        {
            check = f.Check,
            detail = f.Detail,
            amount = f.Amount,
            severity = QualityCheckTextFormatter.StatusText(f.Status)
        }).ToList();

        return new
        {
            id = g.Id,
            name = g.DisplayName,
            grantor = g.Grantor,
            scheme = g.Scheme,
            kind = Grant.KindText(g.Kind),
            status = Grant.StatusText(g.Status),
            link = g.Link,
            applicationDeadline = Iso(g.ApplicationDeadline),
            appliedDate = Iso(g.AppliedDate),
            decisionDate = Iso(g.DecisionDate),
            reportDeadline = Iso(g.ReportDeadline),
            useFrom = Iso(g.UseFrom),
            useTo = Iso(g.UseTo),
            deadlines,
            applied = g.AppliedAmount,
            granted = report.Granted,
            received,
            receivedStatus,
            used = report.Used,
            usedStatus,
            remainingToReceive = remaining,
            unused = GrantOverviewLogic.UnusedOrNull(report),
            otherIncome = report.OtherIncome != 0 ? report.OtherIncome : (decimal?)null,
            usedByAccount = report.UsedByAccount
                .OrderByDescending(a => Math.Abs(a.Amount))
                .Take(top)
                .Select(a => new { account = a.Account, name = a.AccountName, amount = a.Amount, postings = a.Postings.Count })
                .ToList(),
            usedByAccountCount = report.UsedByAccount.Count,
            findings,
            notes
        };
    }

    private static string? FilterText(string? basis, Guid? id, string? name)
    {
        var parts = new List<string>();
        if (basis is not null)
        {
            parts.Add(basis);
        }

        if (id is { } g)
        {
            parts.Add($"id {g}");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add($"navn «{name.Trim()}»");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd");

    private static bool Matches(Grant g, string text) =>
        Contains(g.Name, text) || Contains(g.Grantor, text) || Contains(g.Scheme, text);

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;

    private static GrantStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var text = status.Trim();
        foreach (var value in Enum.GetValues<GrantStatus>())
        {
            if (string.Equals(Grant.StatusText(value), text, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        var options = string.Join(", ", Enum.GetValues<GrantStatus>().Select(Grant.StatusText));
        throw new McpException($"Ukjent status «{text}». Gyldige verdier: {options}.");
    }
}
