using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Purchasing;

public sealed record AccountCandidate(
    string Account,
    string? AccountName,
    int TimesUsed,
    decimal TotalKr,
    string? LastUsed,
    IReadOnlyList<string> ExampleTexts,
    double Score);

public sealed record ProjectCandidate(
    long ProjectId,
    string Name,
    string? Number,
    bool Completed,
    int TimesUsed,
    string? LastUsed,
    IReadOnlyList<string> BudgetLines,
    long? ActiveSuccessorProjectId,
    string? ActiveSuccessorName);

public sealed record PurchaseSuggestion(
    int PurchasesAnalysed,
    IReadOnlyList<AccountCandidate> Accounts,
    IReadOnlyList<ProjectCandidate> Projects,
    IReadOnlyList<string> Notes);

/// <summary>
/// Lager forslag til kostnadskonto og prosjekt ut fra leverandørens tidligere kjøp. Ren logikk uten Fiken-kall.
/// AI-en tar endelig valg sammen med brukeren; dette gir bare rangerte kandidater med begrunnelse.
/// </summary>
public static partial class PurchaseSuggestionBuilder
{
    private const int MaxExampleTexts = 4;

    public static PurchaseSuggestion Build(
        IReadOnlyList<FikenPurchase> supplierPurchases,
        IReadOnlyList<FikenAccount> accounts,
        IReadOnlyList<FikenProject> projects,
        BudgetYear? budgetYear,
        string? documentDescription,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(supplierPurchases);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(projects);

        var accountNames = accounts
            .GroupBy(a => a.Code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var projectById = projects.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First());
        var budgetLinesByProject = (budgetYear?.Lines ?? [])
            .SelectMany(l => l.ProjectIds.Distinct().Select(id => (id, l.Name)))
            .GroupBy(x => x.id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().ToList());
        var descriptionWords = Words(documentDescription);

        var accountStats = new Dictionary<string, AccountStats>(StringComparer.Ordinal);
        var projectStats = new Dictionary<long, (int Count, string? LastUsed)>();
        var purchases = supplierPurchases.Where(p => !p.Deleted).ToList();

        foreach (var purchase in purchases)
        {
            var weight = RecencyWeight(purchase.Date, today);
            foreach (var line in purchase.Lines ?? [])
            {
                if (string.IsNullOrWhiteSpace(line.Account))
                {
                    continue;
                }

                var stats = accountStats.TryGetValue(line.Account, out var existing)
                    ? existing
                    : accountStats[line.Account] = new AccountStats();
                stats.Count++;
                stats.TotalOre += (line.NetPrice ?? 0) + (line.Vat ?? 0);
                stats.Weight += weight;
                stats.LastUsed = Max(stats.LastUsed, purchase.Date);
                if (!string.IsNullOrWhiteSpace(line.Description))
                {
                    stats.Texts.Add(line.Description.Trim());
                    if (descriptionWords.Count > 0 && Words(line.Description).Overlaps(descriptionWords))
                    {
                        stats.Weight += weight;
                    }
                }

                if (line.ProjectId is { } projectId)
                {
                    var (count, last) = projectStats.GetValueOrDefault(projectId);
                    projectStats[projectId] = (count + 1, Max(last, purchase.Date));
                }
            }
        }

        var totalWeight = accountStats.Values.Sum(s => s.Weight);
        var accountCandidates = accountStats
            .Select(kv => new AccountCandidate(
                kv.Key,
                accountNames.GetValueOrDefault(kv.Key) ?? accountNames.GetValueOrDefault(kv.Key.Split(':')[0]),
                kv.Value.Count,
                kv.Value.TotalOre / 100m,
                kv.Value.LastUsed,
                kv.Value.Texts
                    .GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .Take(MaxExampleTexts)
                    .Select(g => g.Key)
                    .ToList(),
                totalWeight > 0 ? Math.Round(kv.Value.Weight / totalWeight, 2) : 0))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.TimesUsed)
            .ToList();

        var activeByNormalizedName = projects
            .Where(p => !p.Completed)
            .GroupBy(p => NormalizeProjectName(p.Name))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.StartDate, StringComparer.Ordinal).First());

        var projectCandidates = projectStats
            .Select(kv =>
            {
                projectById.TryGetValue(kv.Key, out var project);
                var completed = project?.Completed ?? false;
                FikenProject? successor = null;
                if (project is not null && completed
                    && activeByNormalizedName.TryGetValue(NormalizeProjectName(project.Name), out var active)
                    && active.ProjectId != project.ProjectId)
                {
                    successor = active;
                }

                return new ProjectCandidate(
                    kv.Key,
                    project?.Name ?? $"Prosjekt {kv.Key}",
                    project?.Number,
                    completed,
                    kv.Value.Count,
                    kv.Value.LastUsed,
                    budgetLinesByProject.GetValueOrDefault(kv.Key) ?? [],
                    successor?.ProjectId,
                    successor?.Name);
            })
            .OrderBy(p => p.Completed && p.ActiveSuccessorProjectId is null)
            .ThenByDescending(p => p.LastUsed, StringComparer.Ordinal)
            .ThenByDescending(p => p.TimesUsed)
            .ToList();

        var notes = new List<string>();
        if (purchases.Count == 0)
        {
            notes.Add("Ingen tidligere kjøp fra denne leverandøren i perioden. Velg konto ut fra kontoplanen og bilagets innhold.");
        }

        if (accountCandidates.Count > 1)
        {
            notes.Add("Leverandøren er ført på flere kontoer tidligere. Sammenlign linjetekstene med bilaget.");
        }

        if (projectCandidates.Any(p => p.Completed && p.ActiveSuccessorProjectId is not null))
        {
            notes.Add("Noen tidligere prosjekter er avsluttet, men har et aktivt prosjekt med samme navn (f.eks. nytt år).");
        }

        if (purchases.Count > 0 && projectStats.Count == 0)
        {
            notes.Add("Tidligere kjøp fra leverandøren er ikke fordelt på prosjekt.");
        }

        return new PurchaseSuggestion(purchases.Count, accountCandidates, projectCandidates, notes);
    }

    /// <summary>Fjerner årstall, tall og tegnsetting, slik at «Landsleir 2025» og «Landsleir 2026» matcher.</summary>
    public static string NormalizeProjectName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var lowered = name.ToLower(CultureInfo.GetCultureInfo("nb-NO"));
        var withoutDigits = DigitsAndPunctuation().Replace(lowered, " ");
        return string.Join(' ', withoutDigits.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static double RecencyWeight(string? date, DateOnly today)
    {
        if (!DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var parsed))
        {
            return 0.5;
        }

        var ageYears = Math.Max(0, today.DayNumber - parsed.DayNumber) / 365.0;
        return 1.0 / (1.0 + ageYears);
    }

    private static string? Max(string? a, string? b) =>
        string.CompareOrdinal(a, b) >= 0 ? a : b;

    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(char.IsLetter(ch) ? ch : ' ');
        }

        foreach (var word in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 4)
            {
                words.Add(word);
            }
        }

        return words;
    }

    [GeneratedRegex(@"[\d\p{P}\p{S}]+")]
    private static partial Regex DigitsAndPunctuation();

    private sealed class AccountStats
    {
        public int Count;
        public long TotalOre;
        public double Weight;
        public string? LastUsed;
        public List<string> Texts { get; } = [];
    }
}
