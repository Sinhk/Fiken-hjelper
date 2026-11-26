using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>Hvor mange koblede utbetalinger som ligger på kontoen, og hvor mye.</summary>
public sealed record GrantAccountUsage(int Account, string? AccountName, int PaymentCount, decimal Amount);

/// <summary>
/// Lesekontroll: hvilke kontoer utbetalingene som er koblet til Fiken faktisk er ført på,
/// så kassereren ser om tilskudd føres konsekvent.
/// </summary>
public static class GrantAccountOverview
{
    public static IReadOnlyList<GrantAccountUsage> Build(
        IEnumerable<Grant> grants,
        IEnumerable<FikenJournalEntry> journalEntries,
        IReadOnlyList<FikenAccount>? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(journalEntries);

        var linked = grants
            .SelectMany(g => g.Payments ?? [])
            .Where(p => p.IsLinked)
            .Select(p => p.JournalEntryId!.Value)
            .ToHashSet();
        var names = GrantReportBuilder.AccountNames(accounts);

        return GrantReportBuilder.Distinct(journalEntries)
            .Where(e => linked.Contains(e.JournalEntryId))
            .SelectMany(e => (e.Lines ?? [])
                .Select(l => BudgetReportBuilder.TryGetResultAmount(l, out var account, out var amount) && amount > 0
                    ? (Entry: e.JournalEntryId, Account: account, Amount: amount)
                    : default)
                .Where(x => x.Entry != 0))
            .GroupBy(x => x.Account)
            .OrderBy(g => g.Key)
            .Select(g => new GrantAccountUsage(g.Key, names.GetValueOrDefault(g.Key), g.Select(x => x.Entry).Distinct().Count(), g.Sum(x => x.Amount)))
            .ToList();
    }
}
