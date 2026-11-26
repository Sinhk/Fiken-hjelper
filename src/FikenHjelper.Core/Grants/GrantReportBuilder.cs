using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>Et enkeltbilag som er regnet som bruk av et tilskudd (eller som annen inntekt på prosjektet).</summary>
/// <param name="FullAmount">
/// Hele linjens beløp før andel, med fortegn etter kontoklasse: kostnader er positive og en kreditnota eller
/// ompostering på en kostnadskonto er negativ. For annen inntekt er inntekt positiv og en refusjon negativ.
/// </param>
/// <param name="Amount">
/// Tilskuddets del etter andel, avrundet til øre for visning. Summen av bilagene kan avvike noen øre fra kontosummen,
/// som regnes ut fra eksakte beløp og avrundes én gang.
/// </param>
public sealed record GrantPosting(
    long JournalEntryId,
    int? JournalEntryNumber,
    string? Date,
    string? Description,
    int Account,
    long? ProjectId,
    decimal FullAmount,
    decimal Amount);

/// <summary>Sum per konto med bilagene under.</summary>
public sealed record GrantAccountSum(int Account, string? AccountName, decimal Amount, IReadOnlyList<GrantPosting> Postings);

/// <summary>Én mottatt utbetaling. <paramref name="Missing"/> når posteringen er koblet, men ikke finnes i Fiken-dataene.</summary>
public sealed record GrantPaymentReport(GrantPayment Payment, decimal Amount, DateOnly? Date, string? Description, bool Missing);

public enum GrantIssueKind
{
    /// <summary>Andelene for et prosjekt over alle tilskudd summerer til mer enn 100 %.</summary>
    ShareOverAllocated,

    /// <summary>Prosjektet har flere tilskudd, men minst ett mangler andel.</summary>
    ShareMissing,

    /// <summary>Brukt beløp er større enn innvilget.</summary>
    UsedExceedsGranted,

    /// <summary>En koblet utbetaling finnes ikke i Fiken-dataene.</summary>
    PaymentNotFound
}

/// <summary>En kontrollmelding på norsk. <paramref name="GrantIds"/> er tilskuddene meldingen gjelder.</summary>
public sealed record GrantIssue(GrantIssueKind Kind, string Message, IReadOnlyList<Guid> GrantIds, long? ProjectId = null);

public sealed record GrantReport(
    Grant Grant,
    decimal? Granted,
    decimal Received,
    IReadOnlyList<GrantPaymentReport> Payments,
    decimal? Used,
    decimal ManualUsed,
    IReadOnlyList<GrantAccountSum> UsedByAccount,
    IReadOnlyList<GrantAccountSum> OtherIncomeByAccount,
    decimal OtherIncome,
    decimal? Remaining,
    decimal? Unused,
    IReadOnlyList<GrantIssue> Issues,
    bool HasFikenData,
    bool UsedNeedsFikenData = false)
{
    /// <summary>
    /// Brukt er ikke registrert (verken prosjekter, kontoer eller manuell bruk). Er false når bruken er koblet,
    /// men ikke kan regnes ut fordi Fiken-data mangler (se <see cref="UsedNeedsFikenData"/>).
    /// </summary>
    public bool UsedIsUnregistered => Used is null && !UsedNeedsFikenData;
}

/// <summary>
/// Regner ut innvilget, mottatt, brukt og gjenstående for tilskudd fra Fiken-posteringer. Ren logikk.
/// Fortegnsregelen (<c>TryGetResultAmount</c>) hentes fra <see cref="BudgetReportBuilder"/>.
/// </summary>
public static class GrantReportBuilder
{
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    public static IReadOnlyList<GrantReport> BuildAll(
        IReadOnlyList<Grant> grants,
        IEnumerable<FikenJournalEntry>? journalEntries,
        IReadOnlyList<FikenAccount>? accounts = null,
        IReadOnlyList<FikenProject>? projects = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var entries = journalEntries?.ToList();
        return grants.Select(g => Build(g, grants, entries, accounts, projects)).ToList();
    }

    /// <param name="grant">Tilskuddet det regnes for.</param>
    /// <param name="allGrants">Alle tilskudd, for andeler på prosjekter som deles og posteringer som er koblet andre steder.</param>
    /// <param name="journalEntries">Posteringer for alle år som er aktuelle (se <see cref="YearsToFetch"/>), eller null uten Fiken-data.</param>
    public static GrantReport Build(
        Grant grant,
        IReadOnlyList<Grant> allGrants,
        IEnumerable<FikenJournalEntry>? journalEntries,
        IReadOnlyList<FikenAccount>? accounts = null,
        IReadOnlyList<FikenProject>? projects = null)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(allGrants);

        var hasFikenData = journalEntries is not null;
        var entries = Distinct(journalEntries);
        var accountNames = AccountNames(accounts);
        var issues = new List<GrantIssue>();
        // Med forhåndsvisning av et redigert tilskudd er det den redigerte versjonen som gjelder, ikke den lagrede.
        var effective = WithGrant(allGrants, grant);

        // Utbetalinger.
        var byId = entries.ToDictionary(e => e.JournalEntryId);
        var payments = new List<GrantPaymentReport>();
        foreach (var payment in grant.Payments ?? [])
        {
            if (payment.IsLinked)
            {
                var found = byId.TryGetValue(payment.JournalEntryId!.Value, out var entry);
                var missing = hasFikenData && !found;
                payments.Add(new GrantPaymentReport(
                    payment,
                    found ? IncomeAmount(entry!) : 0m,
                    found ? ParseDate(entry!.Date) ?? payment.Date : payment.Date,
                    found ? entry!.Description : payment.Note,
                    missing));
                if (missing)
                {
                    issues.Add(new GrantIssue(
                        GrantIssueKind.PaymentNotFound,
                        $"Utbetalingen koblet til Fiken-postering {payment.JournalEntryId} finnes ikke i hentede posteringer. Beløpet er ikke regnet med.",
                        [grant.Id]));
                }
            }
            else
            {
                payments.Add(new GrantPaymentReport(payment, Round(payment.Amount ?? 0m), payment.Date, payment.Note, false));
            }
        }

        var received = payments.Sum(p => p.Amount);

        // Bruk. Prosjektlogikk gjelder bare prosjekttilskudd, kontologikk bare driftstilskudd.
        var linkedEntryIds = effective
            .SelectMany(g => g.Payments ?? [])
            .Where(p => p.IsLinked)
            .Select(p => p.JournalEntryId!.Value)
            .ToHashSet();
        var used = new List<(GrantPosting Posting, decimal Exact)>();
        var income = new List<(GrantPosting Posting, decimal Exact)>();
        var isProject = grant.Kind == GrantKind.Project;
        var projectSet = isProject ? (grant.ProjectIds ?? []).ToHashSet() : [];
        var ranges = isProject ? [] : grant.AccountRanges ?? [];
        var manual = isProject ? [] : grant.ManualUses ?? [];

        if (hasFikenData)
        {
            foreach (var entry in entries)
            {
                if (!InPeriod(ParseDate(entry.Date), grant.UseFrom, grant.UseTo))
                {
                    continue;
                }

                foreach (var line in entry.Lines ?? [])
                {
                    if (!BudgetReportBuilder.TryGetResultAmount(line, out var account, out var amount))
                    {
                        continue;
                    }

                    var projectId = BudgetReportBuilder.PrimaryProjectId(line);
                    if (projectId is { } p && projectSet.Contains(p))
                    {
                        if (IsCostAccount(account))
                        {
                            // Kostnadskonti: debet er bruk, kredit (kreditnota, ompostering) reduserer bruken.
                            var exact = -amount * (grant.ShareOf(p) ?? 100m) / 100m;
                            used.Add((ToPosting(entry, account, p, -amount, Round(exact)), exact));
                        }
                        else if (IsIncomeAccount(account) && !linkedEntryIds.Contains(entry.JournalEntryId))
                        {
                            income.Add((ToPosting(entry, account, p, amount, amount), amount));
                        }
                    }
                    else if (projectId is null && IsCostAccount(account) && ranges.Any(r => r.Contains(account)))
                    {
                        used.Add((ToPosting(entry, account, null, -amount, -amount), -amount));
                    }
                }
            }
        }

        var usedByAccount = GroupByAccount(used, accountNames);
        var manualUsed = Round(manual.Sum(u => u.Amount));
        var fikenSource = projectSet.Count > 0 || ranges.Count > 0;
        var needsFiken = fikenSource && !hasFikenData;
        decimal? usedTotal = !needsFiken && (fikenSource || manual.Count > 0)
            ? usedByAccount.Sum(a => a.Amount) + manualUsed
            : null;

        var otherByAccount = GroupByAccount(income, accountNames);
        decimal? granted = grant.GrantedAmount;

        if (usedTotal is { } u && granted is { } gr && u > gr)
        {
            issues.Add(new GrantIssue(
                GrantIssueKind.UsedExceedsGranted,
                $"«{grant.DisplayName}»: brukt ({Money(u)}) er mer enn innvilget ({Money(gr)}).",
                [grant.Id]));
        }

        issues.AddRange(CheckProjectShares(effective, projects).Where(i => i.GrantIds.Contains(grant.Id)));

        return new GrantReport(
            grant,
            granted,
            received,
            payments,
            usedTotal,
            manualUsed,
            usedByAccount,
            otherByAccount,
            otherByAccount.Sum(a => a.Amount),
            granted is null ? null : granted.Value - received,
            usedTotal is null ? null : received - usedTotal.Value,
            issues,
            hasFikenData,
            needsFiken);
    }

    /// <summary>
    /// Kostnadskonti: 4000–7999 og 8100–8799 (finans og skatt). Bruk regnes etter kontoklasse, ikke etter fortegn,
    /// så en kreditnota eller ompostering reduserer bruken.
    /// </summary>
    public static bool IsCostAccount(int account) => account is >= 4000 and <= 7999 or >= 8100 and <= 8799;

    /// <summary>Inntektskonti: 3000–3999 og 8000–8099 (finansinntekter). En refusjon reduserer inntekten.</summary>
    public static bool IsIncomeAccount(int account) => account is >= 3000 and <= 3999 or >= 8000 and <= 8099;

    /// <summary>Lager listen over alle tilskudd der <paramref name="grant"/> erstatter det med samme Id (eller legges til).</summary>
    internal static IReadOnlyList<Grant> WithGrant(IEnumerable<Grant> allGrants, Grant grant)
    {
        var list = allGrants.Select(g => g.Id == grant.Id ? grant : g).ToList();
        if (!list.Contains(grant))
        {
            list.Add(grant);
        }

        return list;
    }

    /// <summary>
    /// Kontrollerer andelene for hvert prosjekt over alle aktive prosjekttilskudd (ikke avslått, trukket eller driftstilskudd):
    /// over 100 % i sum, eller manglende andel når prosjektet har flere tilskudd. Tilsvarer «ugyldig fordeling» i Fase 3.
    /// </summary>
    public static IReadOnlyList<GrantIssue> CheckProjectShares(
        IEnumerable<Grant> grants,
        IReadOnlyList<FikenProject>? projects = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var names = (projects ?? [])
            .GroupBy(p => p.ProjectId)
            .ToDictionary(g => g.Key, g => g.First().Name);
        var issues = new List<GrantIssue>();

        var byProject = grants
            .Where(g => g.IsActive && g.Kind == GrantKind.Project)
            .SelectMany(g => (g.ProjectIds ?? []).Distinct().Select(id => (Id: id, Grant: g)))
            .GroupBy(x => x.Id)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key);

        foreach (var group in byProject)
        {
            var linked = group.Select(x => x.Grant).ToList();
            var name = names.GetValueOrDefault(group.Key) ?? $"Prosjekt {group.Key}";
            var list = string.Join(", ", linked.Select(g => $"«{g.DisplayName}»"));
            var ids = linked.Select(g => g.Id).ToList();

            if (linked.Any(g => g.ShareOf(group.Key) is null))
            {
                issues.Add(new GrantIssue(
                    GrantIssueKind.ShareMissing,
                    $"«{name}» er koblet til {list}, men mangler fordelingsnøkkel.",
                    ids,
                    group.Key));
            }
            else
            {
                var total = linked.Sum(g => g.ShareOf(group.Key)!.Value);
                if (total > 100m)
                {
                    issues.Add(new GrantIssue(
                        GrantIssueKind.ShareOverAllocated,
                        $"Andelene for «{name}» på {list} summerer til {total.ToString("0.##", Nb)} %, mer enn 100 %.",
                        ids,
                        group.Key));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Hvilke år posteringer må hentes for: fra tidligste kjente dato (bruksperiode, vedtak eller søknad, rapporteringsfrist
    /// og koblede utbetalinger) til siste kjente dato. Et tilskudd som ikke er avsluttet, avslått eller trukket hentes
    /// til og med <paramref name="currentYear"/>, så sene utbetalinger kan foreslås. Aldri forbi <paramref name="currentYear"/>.
    /// </summary>
    public static IReadOnlyList<int> YearsToFetch(Grant grant, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var years = new[] { grant.UseFrom, grant.UseTo, grant.DecisionDate ?? grant.AppliedDate, grant.ReportDeadline }
            .Concat((grant.Payments ?? []).Where(p => p.IsLinked).Select(p => p.Date))
            .Where(d => d.HasValue)
            .Select(d => d!.Value.Year)
            .ToList();

        var start = Math.Min(years.Count > 0 ? years.Min() : currentYear, currentYear);
        var end = years.Count > 0 ? years.Max() : currentYear;
        if (grant.Status is not (GrantStatus.Closed or GrantStatus.Rejected or GrantStatus.Withdrawn))
        {
            end = currentYear;
        }

        end = Math.Clamp(end, start, currentYear);
        return Enumerable.Range(start, end - start + 1).ToList();
    }

    internal static IReadOnlyList<FikenJournalEntry> Distinct(IEnumerable<FikenJournalEntry>? entries) =>
        (entries ?? []).Where(e => e is not null).DistinctBy(e => e.JournalEntryId).ToList();

    internal static Dictionary<int, string> AccountNames(IReadOnlyList<FikenAccount>? accounts)
    {
        var names = new Dictionary<int, string>();
        // Hovedkontoer (uten «:») går foran underkontoer, så kontonavnet ikke blir et kundenavn.
        foreach (var account in (accounts ?? []).OrderBy(a => a.Code?.Contains(':') == true))
        {
            if (BudgetReportBuilder.TryParseAccount(account.Code, out var number))
            {
                names.TryAdd(number, account.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Netto inntekt i posteringen i kroner: resultatbidraget på inntektskonti (en refusjon reduserer).
    /// Den ene regelen for «mottatt», også når forslag til utbetalinger viser beløp.
    /// </summary>
    internal static decimal IncomeAmount(FikenJournalEntry entry) => IncomeLines(entry).Sum(l => l.Amount);

    /// <summary>Inntektskontoene (og beløpene) i posteringen, i kontorekkefølge.</summary>
    internal static IReadOnlyList<(int Account, decimal Amount)> IncomeLines(FikenJournalEntry entry) =>
        (entry.Lines ?? [])
            .Select(l => BudgetReportBuilder.TryGetResultAmount(l, out var account, out var amount) && IsIncomeAccount(account)
                ? (Account: account, Amount: amount)
                : default)
            .Where(x => x.Account != 0)
            .OrderBy(x => x.Account)
            .ToList();

    internal static DateOnly? ParseDate(string? date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static bool InPeriod(DateOnly? date, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return true;
        }

        return date is { } d && (from is null || d >= from) && (to is null || d <= to);
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static string Money(decimal amount) => amount.ToString("N2", Nb);

    private static GrantPosting ToPosting(FikenJournalEntry entry, int account, long? projectId, decimal full, decimal share) =>
        new(entry.JournalEntryId, entry.JournalEntryNumber, entry.Date, entry.Description, account, projectId, full, share);

    /// <summary>Summerer eksakte beløp per konto og avrunder én gang per konto.</summary>
    private static IReadOnlyList<GrantAccountSum> GroupByAccount(
        IEnumerable<(GrantPosting Posting, decimal Exact)> postings,
        IReadOnlyDictionary<int, string> names) =>
        postings
            .GroupBy(p => p.Posting.Account)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var list = g.Select(x => x.Posting)
                    .OrderBy(p => p.Date, StringComparer.Ordinal)
                    .ThenBy(p => p.JournalEntryNumber)
                    .ToList();
                return new GrantAccountSum(g.Key, names.GetValueOrDefault(g.Key), Round(g.Sum(x => x.Exact)), list);
            })
            .ToList();
}
