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
    decimal Amount)
{
    /// <summary>Andelen i prosent som teller her, når prosjektet er fordelt på flere poster.</summary>
    public decimal? Share { get; init; }
}

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
        return Explain(year, projectIds.Distinct().ToDictionary(id => id, _ => 1m), entries, previousYearEntries, accounts, maxPostings);
    }

    /// <summary>
    /// Som over, men med en andel (0–1) per prosjekt for prosjekter fordelt på flere budsjettposter
    /// (<see cref="ProjectSplits.LineFractions"/>). Beløpene ganges med andelen, og summene avrundes til øre.
    /// </summary>
    public static ProjectVarianceExplanation Explain(
        int year,
        IReadOnlyDictionary<long, decimal> projectFractions,
        IReadOnlyList<FikenJournalEntry> entries,
        IReadOnlyList<FikenJournalEntry>? previousYearEntries,
        IReadOnlyList<FikenAccount> accounts,
        int maxPostings)
    {
        ArgumentNullException.ThrowIfNull(projectFractions);
        ArgumentNullException.ThrowIfNull(entries);

        var wanted = projectFractions;
        var accountNames = (accounts ?? [])
            .GroupBy(a => a.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var postings = Collect(entries, wanted);
        var previous = previousYearEntries is null ? null : Collect(previousYearEntries, wanted);
        var previousByAccount = previous?
            .GroupBy(p => MainAccount(p.Account))
            .ToDictionary(g => g.Key, g => Round(g.Sum(p => p.Amount)), StringComparer.Ordinal);

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
                    Round(current.Sum(p => p.Amount)),
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
            .Select(p => p with { Amount = Round(p.Amount) })
            .ToList();

        return new ProjectVarianceExplanation(
            year,
            wanted.Keys.Order().ToList(),
            Round(postings.Sum(p => p.Amount)),
            previous is null ? null : Round(previous.Sum(p => p.Amount)),
            postings.Count,
            byAccount,
            largest);
    }

    private static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static List<PostingDetail> Collect(IReadOnlyList<FikenJournalEntry> entries, IReadOnlyDictionary<long, decimal> wanted)
    {
        var result = new List<PostingDetail>();
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines ?? [])
            {
                if (!BudgetReportBuilder.TryGetResultAmount(line, out _, out var amount)
                    || BudgetReportBuilder.PrimaryProjectId(line) is not { } projectId
                    || !wanted.TryGetValue(projectId, out var fraction))
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
                    amount * fraction)
                {
                    Share = fraction == 1m ? null : fraction * 100m
                });
            }
        }

        return result;
    }

    private static string MainAccount(string account) => account.Split(':', 2)[0].Trim();
}
