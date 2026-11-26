using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>Et enkeltbilag som er regnet som bruk av et tilskudd.</summary>
/// <param name="FullAmount">Hele linjens beløp (positivt for kostnad, negativt for inntekt).</param>
/// <param name="Amount">Tilskuddets del etter andel, avrundet til øre.</param>
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
    bool HasFikenData)
{
    /// <summary>Brukt er ikke registrert (verken prosjekter, kontoer eller manuell bruk).</summary>
    public bool UsedIsUnregistered => Used is null;
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

        // Bruk.
        var linkedEntryIds = allGrants
            .SelectMany(g => g.Payments ?? [])
            .Where(p => p.IsLinked)
            .Select(p => p.JournalEntryId!.Value)
            .ToHashSet();
        var usedPostings = new List<GrantPosting>();
        var otherIncome = new List<GrantPosting>();
        var projectSet = (grant.ProjectIds ?? []).ToHashSet();
        var ranges = grant.Kind == GrantKind.Operating ? grant.AccountRanges ?? [] : [];
        var manual = grant.Kind == GrantKind.Operating ? grant.ManualUses ?? [] : [];

        if (hasFikenData)
        {
            foreach (var entry in entries)
            {
                var date = ParseDate(entry.Date);
                if (!InPeriod(date, grant.UseFrom, grant.UseTo))
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
                        if (amount < 0)
                        {
                            var cost = -amount;
                            var fraction = (grant.ShareOf(p) ?? 100m) / 100m;
                            usedPostings.Add(ToPosting(entry, account, p, cost, Round(cost * fraction)));
                        }
                        else if (amount > 0 && !linkedEntryIds.Contains(entry.JournalEntryId))
                        {
                            otherIncome.Add(ToPosting(entry, account, p, amount, amount));
                        }
                    }
                    else if (projectId is null && amount < 0 && ranges.Any(r => r.Contains(account)))
                    {
                        usedPostings.Add(ToPosting(entry, account, null, -amount, -amount));
                    }
                }
            }
        }

        var usedByAccount = GroupByAccount(usedPostings, accountNames);
        var manualUsed = manual.Sum(u => u.Amount);
        var registered = projectSet.Count > 0 && grant.Kind == GrantKind.Project || ranges.Count > 0 || manual.Count > 0;
        decimal? used = registered && (hasFikenData || manual.Count > 0)
            ? usedByAccount.Sum(a => a.Amount) + manualUsed
            : null;

        var otherByAccount = GroupByAccount(otherIncome, accountNames);
        decimal? granted = grant.GrantedAmount;

        if (used is { } u && granted is { } gr && u > gr)
        {
            issues.Add(new GrantIssue(
                GrantIssueKind.UsedExceedsGranted,
                $"«{grant.DisplayName}»: brukt ({Money(u)}) er mer enn innvilget ({Money(gr)}).",
                [grant.Id]));
        }

        issues.AddRange(CheckProjectShares(allGrants, projects).Where(i => i.GrantIds.Contains(grant.Id)));

        return new GrantReport(
            grant,
            granted,
            received,
            payments,
            used,
            manualUsed,
            usedByAccount,
            otherByAccount,
            otherByAccount.Sum(a => a.Amount),
            granted is null ? null : granted.Value - received,
            used is null ? null : received - used.Value,
            issues,
            hasFikenData);
    }

    /// <summary>
    /// Kontrollerer andelene for hvert prosjekt over alle aktive tilskudd (ikke avslått eller trukket):
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
            .Where(g => g.IsActive)
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
    /// Hvilke år posteringer må hentes for: fra tidligste kjente dato (bruksperiode, vedtak, søknad, utbetalinger)
    /// til slutten av bruksperioden, eller <paramref name="currentYear"/> når perioden er åpen.
    /// </summary>
    public static IReadOnlyList<int> YearsToFetch(Grant grant, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var dates = new[] { grant.UseFrom, grant.UseTo, grant.DecisionDate, grant.AppliedDate }
            .Concat((grant.Payments ?? []).Select(p => p.Date))
            .Where(d => d.HasValue)
            .Select(d => d!.Value.Year)
            .ToList();

        var start = dates.Count > 0 ? dates.Min() : currentYear;
        var end = grant.UseTo?.Year ?? currentYear;
        end = Math.Max(end, dates.Count > 0 ? dates.Max() : end);
        end = Math.Max(end, start);
        return Enumerable.Range(start, end - start + 1).ToList();
    }

    internal static IReadOnlyList<FikenJournalEntry> Distinct(IEnumerable<FikenJournalEntry>? entries) =>
        (entries ?? []).Where(e => e is not null).DistinctBy(e => e.JournalEntryId).ToList();

    internal static Dictionary<int, string> AccountNames(IReadOnlyList<FikenAccount>? accounts)
    {
        var names = new Dictionary<int, string>();
        foreach (var account in accounts ?? [])
        {
            if (BudgetReportBuilder.TryParseAccount(account.Code, out var number))
            {
                names.TryAdd(number, account.Name);
            }
        }

        return names;
    }

    /// <summary>Summen av inntektsretning (kreditt) på resultatkontoer i posteringen, i kroner.</summary>
    internal static decimal IncomeAmount(FikenJournalEntry entry) =>
        (entry.Lines ?? [])
            .Select(l => BudgetReportBuilder.TryGetResultAmount(l, out _, out var amount) ? amount : 0m)
            .Where(a => a > 0)
            .Sum();

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

    private static IReadOnlyList<GrantAccountSum> GroupByAccount(IEnumerable<GrantPosting> postings, IReadOnlyDictionary<int, string> names) =>
        postings
            .GroupBy(p => p.Account)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var list = g.OrderBy(p => p.Date, StringComparer.Ordinal).ThenBy(p => p.JournalEntryNumber).ToList();
                return new GrantAccountSum(g.Key, names.GetValueOrDefault(g.Key), list.Sum(p => p.Amount), list);
            })
            .ToList();
}
