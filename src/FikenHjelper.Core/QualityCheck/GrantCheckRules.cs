using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Grants;

namespace FikenHjelper.Core.QualityCheck;

/// <summary>
/// Kontrollene for tilskudd i kvalitetssjekken. Bygger på <see cref="GrantService.BuildOverview"/>,
/// <see cref="GrantOverviewLogic.Deadlines"/> og <see cref="GrantReportBuilder"/>, så regnereglene er de samme som på tilskuddssidene.
/// Ren logikk uten HTTP.
/// </summary>
public static class GrantCheckRules
{
    /// <summary>Antall dager etter vedtaket før et tilskudd som ikke er (fullt) mottatt, meldes.</summary>
    public const int NotReceivedDays = 60;

    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    /// <param name="grants">Alle tilskudd.</param>
    /// <param name="data">Fiken-data (<see cref="GrantFikenData.None"/> når de mangler).</param>
    /// <param name="referenceDate">«Per dato»: frister og dager regnes fra denne, og posteringer etter den tas ikke med.</param>
    /// <param name="loadError">Feilmelding når Fiken-data ikke kunne hentes, vises i stedet for en generell merknad.</param>
    public static GrantCheckSection Build(
        IReadOnlyList<Grant> grants,
        GrantFikenData data,
        DateOnly referenceDate,
        string? loadError = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(data);

        var findings = new List<GrantCheckFinding>();
        var hasFiken = data.Entries is not null;
        var overview = GrantService.BuildOverview(grants, data);
        var reports = overview.Reports.ToDictionary(r => r.Grant.Id);

        foreach (var grant in grants)
        {
            foreach (var deadline in GrantOverviewLogic.Deadlines(grant, referenceDate))
            {
                var overdue = deadline.State == GrantDeadlineState.Overdue;
                var what = deadline.Kind == GrantDeadlineKind.Application ? "Søknadsfrist" : "Rapporteringsfrist";
                findings.Add(new GrantCheckFinding(
                    grant.Id,
                    grant.DisplayName,
                    deadline.Kind == GrantDeadlineKind.Application ? GrantCheckKind.ApplicationDeadline : GrantCheckKind.ReportDeadline,
                    overdue ? $"{what} er passert" : $"{what} nærmer seg",
                    $"{Date(deadline.Date)} ({DaysText(deadline.Days)})",
                    null,
                    overdue ? QualityStatus.Overdue : QualityStatus.Open));
            }

            if (reports.TryGetValue(grant.Id, out var report))
            {
                AddNotReceived(findings, grant, report, referenceDate);
            }

            if (hasFiken)
            {
                AddCostsOutsidePeriod(findings, grant, data, referenceDate);
            }
        }

        foreach (var issue in overview.Issues)
        {
            var (kind, check) = issue.Kind switch
            {
                GrantIssueKind.UsedExceedsGranted => (GrantCheckKind.UsedExceedsGranted, "Brukt mer enn innvilget"),
                GrantIssueKind.PaymentNotFound => (GrantCheckKind.PaymentNotFound, "Utbetaling finnes ikke i Fiken"),
                _ => (GrantCheckKind.InvalidShares, "Ugyldig andelsfordeling")
            };

            foreach (var id in issue.GrantIds.Distinct())
            {
                var grant = grants.FirstOrDefault(g => g.Id == id);
                if (grant is null)
                {
                    continue;
                }

                decimal? amount = null;
                if (kind == GrantCheckKind.UsedExceedsGranted
                    && reports.TryGetValue(id, out var r) && r.Used is { } used && r.Granted is { } granted)
                {
                    amount = used - granted;
                }

                findings.Add(new GrantCheckFinding(id, grant.DisplayName, kind, check, issue.Message, amount, QualityStatus.CheckManually));
            }
        }

        var sorted = findings
            .OrderBy(f => f.GrantName, StringComparer.Create(Nb, ignoreCase: true))
            .ThenBy(f => f.Kind)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)
            .ToList();
        return new GrantCheckSection(sorted, grants.Count, MissingNote(grants, data, loadError));
    }

    private static string? MissingNote(IReadOnlyList<Grant> grants, GrantFikenData data, string? loadError)
    {
        if (data.Entries is not null)
        {
            return null;
        }

        const string Skipped = "Kontrollene «Brukt mer enn innvilget» (for koblede prosjekter), «Innvilget, men ikke mottatt» (for koblede utbetalinger), "
            + "«Kostnader utenfor bruksperioden» og «Utbetaling finnes ikke i Fiken» er ikke kjørt. Frister og andelsfordeling er kontrollert.";
        var reason = string.IsNullOrWhiteSpace(loadError)
            ? "Mangler Fiken-data: ingen Fiken-foretak er valgt."
            : $"Mangler Fiken-data: {loadError.Trim()}";
        return grants.Count == 0 ? reason : $"{reason} {Skipped}";
    }

    private static void AddNotReceived(List<GrantCheckFinding> findings, Grant grant, GrantReport report, DateOnly referenceDate)
    {
        if (grant.Status is not (GrantStatus.Granted or GrantStatus.PartiallyGranted)
            || grant.DecisionDate is not { } decision
            || report.Granted is not { } granted)
        {
            return;
        }

        var days = referenceDate.DayNumber - decision.DayNumber;
        if (days < NotReceivedDays)
        {
            return;
        }

        // Ukjent mottatt (koblet utbetaling uten Fiken-data) gir ingen funn, bare en merknad i seksjonen.
        if (GrantOverviewLogic.RemainingOrNull(report) is not { } remaining || remaining <= 0)
        {
            return;
        }

        var received = report.Received;
        findings.Add(new GrantCheckFinding(
            grant.Id,
            grant.DisplayName,
            GrantCheckKind.NotReceived,
            $"{Grant.StatusText(grant.Status)}, men ikke fullt mottatt etter {NotReceivedDays} dager",
            $"Vedtak {Date(decision)} ({days} dager siden). Innvilget {Money(granted)}, mottatt {Money(received)}, gjenstår {Money(remaining)}.",
            remaining,
            QualityStatus.Overdue));
    }

    private static void AddCostsOutsidePeriod(List<GrantCheckFinding> findings, Grant grant, GrantFikenData data, DateOnly referenceDate)
    {
        if (grant.Kind != GrantKind.Project || !grant.IsActive || (grant.UseFrom is null && grant.UseTo is null) || (grant.ProjectIds?.Count ?? 0) == 0)
        {
            return;
        }

        var projects = grant.ProjectIds!.ToHashSet();
        decimal sum = 0;
        var vouchers = new HashSet<long>();
        foreach (var entry in GrantReportBuilder.Distinct(data.Entries))
        {
            if (GrantReportBuilder.ParseDate(entry.Date) is not { } date || date > referenceDate)
            {
                continue;
            }

            var outside = (grant.UseFrom is { } from && date < from) || (grant.UseTo is { } to && date > to);
            if (!outside)
            {
                continue;
            }

            foreach (var line in entry.Lines ?? [])
            {
                if (BudgetReportBuilder.TryGetResultAmount(line, out var account, out var amount)
                    && GrantReportBuilder.IsCostAccount(account)
                    && BudgetReportBuilder.PrimaryProjectId(line) is { } projectId
                    && projects.Contains(projectId))
                {
                    sum += -amount;
                    vouchers.Add(entry.JournalEntryId);
                }
            }
        }

        sum = Math.Round(sum, 2, MidpointRounding.AwayFromZero);
        if (vouchers.Count == 0 || sum == 0)
        {
            return;
        }

        var period = $"{(grant.UseFrom is { } pf ? Date(pf) : "…")}–{(grant.UseTo is { } pt ? Date(pt) : "…")}";
        findings.Add(new GrantCheckFinding(
            grant.Id,
            grant.DisplayName,
            GrantCheckKind.CostsOutsidePeriod,
            "Kostnader utenfor bruksperioden",
            $"{Money(sum)} fordelt på {vouchers.Count} bilag på koblede prosjekter utenfor bruksperioden {period}.",
            sum,
            QualityStatus.CheckManually));
    }

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("N2", Nb) + " kr";

    private static string DaysText(int days) => days switch
    {
        0 => "i dag",
        1 => "om 1 dag",
        > 1 => $"om {days} dager",
        -1 => "1 dag siden",
        _ => $"{-days} dager siden"
    };
}
