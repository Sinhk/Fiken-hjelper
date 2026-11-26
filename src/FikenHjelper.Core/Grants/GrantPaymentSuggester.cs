using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>Et forslag til utbetaling i Fiken. Brukeren velger selv, og først da kobles posteringen.</summary>
/// <param name="Amount">Inntekten på posteringens aktuelle resultatlinjer, i kroner.</param>
/// <param name="MatchesAmount">Beløpet er lik innvilget eller det som gjenstår å motta.</param>
/// <param name="NameScore">Likhet 0–100 mellom tilskuddsgiver og posteringsteksten (0 under terskelen).</param>
public sealed record GrantPaymentSuggestion(
    long JournalEntryId,
    int? JournalEntryNumber,
    string? Date,
    string? Description,
    decimal Amount,
    IReadOnlyList<int> Accounts,
    bool OnLinkedProject,
    bool MatchesAmount,
    int NameScore);

/// <summary>
/// Finner inntektsposteringer som kan være utbetaling av et tilskudd. Bygger ikke på en fast tilskuddskonto,
/// siden det er usikkert om tilskudd er ført konsekvent.
/// </summary>
public static class GrantPaymentSuggester
{
    public static bool IsBroadIncomeAccount(int account) =>
        account is >= 3400 and <= 3499 or >= 3900 and <= 3999;

    public static IReadOnlyList<GrantPaymentSuggestion> Suggest(
        Grant grant,
        IReadOnlyList<Grant> allGrants,
        IEnumerable<FikenJournalEntry> journalEntries)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(allGrants);
        ArgumentNullException.ThrowIfNull(journalEntries);

        var entries = GrantReportBuilder.Distinct(journalEntries);
        var from = grant.DecisionDate ?? grant.AppliedDate;
        var projects = grant.Kind == GrantKind.Project ? (grant.ProjectIds ?? []).ToHashSet() : [];
        var effective = GrantReportBuilder.WithGrant(allGrants, grant);

        // Posteringer som allerede er koblet til et tilskudd (også dette) foreslås ikke på nytt.
        var taken = effective
            .SelectMany(g => g.Payments ?? [])
            .Where(p => p.IsLinked)
            .Select(p => p.JournalEntryId!.Value)
            .ToHashSet();

        var linkedToThis = (grant.Payments ?? []).Where(p => p.IsLinked).Select(p => p.JournalEntryId!.Value).ToHashSet();
        var received = GrantReportBuilder.Build(grant, effective, entries).Received;
        var remaining = grant.GrantedAmount is { } granted ? granted - received : (decimal?)null;

        var result = new List<GrantPaymentSuggestion>();
        foreach (var entry in entries)
        {
            if (taken.Contains(entry.JournalEntryId) || linkedToThis.Contains(entry.JournalEntryId))
            {
                continue;
            }

            var date = GrantReportBuilder.ParseDate(entry.Date);
            if (from is not null && (date is null || date < from))
            {
                continue;
            }

            // En postering kvalifiserer når en inntektslinje ligger på et koblet prosjekt eller (uten prosjekt) på 34xx/39xx.
            // Beløp og kontoer regnes deretter som for «mottatt», så tallet ikke endrer seg ved kobling.
            var qualifies = (entry.Lines ?? []).Any(line =>
                BudgetReportBuilder.TryGetResultAmount(line, out var account, out _)
                && GrantReportBuilder.IsIncomeAccount(account)
                && (BudgetReportBuilder.PrimaryProjectId(line) is { } p
                    ? projects.Contains(p)
                    : IsBroadIncomeAccount(account)));
            var amount = GrantReportBuilder.IncomeAmount(entry);
            if (!qualifies || amount <= 0)
            {
                continue;
            }

            var onProject = (entry.Lines ?? []).Any(l => BudgetReportBuilder.PrimaryProjectId(l) is { } p && projects.Contains(p));
            var accounts = GrantReportBuilder.IncomeLines(entry).Select(l => l.Account).Distinct().ToList();

            var matches = (grant.GrantedAmount is { } g && amount == g) || (remaining is { } r && r > 0 && amount == r);
            var score = ProjectNameMatcher.Score(grant.Grantor, entry.Description);
            result.Add(new GrantPaymentSuggestion(
                entry.JournalEntryId,
                entry.JournalEntryNumber,
                entry.Date,
                entry.Description,
                amount,
                accounts,
                onProject,
                matches,
                score >= ProjectNameMatcher.SuggestionThreshold ? score : 0));
        }

        return result
            .OrderByDescending(s => s.MatchesAmount)
            .ThenByDescending(s => s.NameScore)
            .ThenBy(s => s.Date, StringComparer.Ordinal)
            .ThenBy(s => s.JournalEntryNumber)
            .ToList();
    }
}
