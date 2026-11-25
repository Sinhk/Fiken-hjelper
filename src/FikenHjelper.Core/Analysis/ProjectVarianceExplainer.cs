using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Analysis;

public sealed record AccountBreakdown(
    string Account,
    string? AccountName,
    decimal Amount,
    decimal? PreviousYearAmount,
    int Postings);

public sealed record PostingDetail(
    long JournalEntryId,
    int? JournalEntryNumber,
    string? Date,
    string? Description,
    string Account,
    long ProjectId,
    decimal Amount);

public sealed record ProjectVarianceExplanation(
    int Year,
    IReadOnlyList<long> ProjectIds,
    decimal Actual,
    decimal? PreviousYearActual,
    int PostingCount,
    IReadOnlyList<AccountBreakdown> ByAccount,
    IReadOnlyList<PostingDetail> LargestPostings);

/// <summary>
/// Bryter ned regnskapet for ett eller flere prosjekter per konto og enkeltposteringer. Bruker samme regel som
/// budsjettrapporten (<see cref="BudgetReportBuilder.TryGetResultAmount"/> og <see cref="BudgetReportBuilder.PrimaryProjectId"/>),
/// slik at summene stemmer med Fase 3.
/// </summary>
public static class ProjectVarianceExplainer
{
    public static ProjectVarianceExplanation Explain(
        int year,
        IReadOnlyCollection<long> projectIds,
        IReadOnlyList<FikenJournalEntry> entries,
        IReadOnlyList<FikenJournalEntry>? previousYearEntries,
        IReadOnlyList<FikenAccount> accounts,
        int maxPostings)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(entries);

        var wanted = projectIds.ToHashSet();
        var accountNames = (accounts ?? [])
            .GroupBy(a => a.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var postings = Collect(entries, wanted);
        var previous = previousYearEntries is null ? null : Collect(previousYearEntries, wanted);
        var previousByAccount = previous?
            .GroupBy(p => MainAccount(p.Account))
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount), StringComparer.Ordinal);

        var accountKeys = postings.Select(p => MainAccount(p.Account))
            .Concat(previousByAccount?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal);

        var byAccount = accountKeys
            .Select(account =>
            {
                var current = postings.Where(p => MainAccount(p.Account) == account).ToList();
                return new AccountBreakdown(
                    account,
                    accountNames.GetValueOrDefault(account),
                    current.Sum(p => p.Amount),
                    previousByAccount is null ? null : previousByAccount.GetValueOrDefault(account),
                    current.Count);
            })
            .OrderBy(a => a.Amount)
            .ThenBy(a => a.Account, StringComparer.Ordinal)
            .ToList();

        var largest = postings
            .OrderByDescending(p => Math.Abs(p.Amount))
            .ThenBy(p => p.Date, StringComparer.Ordinal)
            .Take(Math.Clamp(maxPostings, 0, 200))
            .ToList();

        return new ProjectVarianceExplanation(
            year,
            wanted.Order().ToList(),
            postings.Sum(p => p.Amount),
            previous?.Sum(p => p.Amount),
            postings.Count,
            byAccount,
            largest);
    }

    private static List<PostingDetail> Collect(IReadOnlyList<FikenJournalEntry> entries, HashSet<long> wanted)
    {
        var result = new List<PostingDetail>();
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines ?? [])
            {
                if (!BudgetReportBuilder.TryGetResultAmount(line, out _, out var amount)
                    || BudgetReportBuilder.PrimaryProjectId(line) is not { } projectId
                    || !wanted.Contains(projectId))
                {
                    continue;
                }

                result.Add(new PostingDetail(
                    entry.JournalEntryId,
                    entry.JournalEntryNumber,
                    entry.Date,
                    entry.Description,
                    line.Account!,
                    projectId,
                    amount));
            }
        }

        return result;
    }

    private static string MainAccount(string account) => account.Split(':', 2)[0].Trim();
}
