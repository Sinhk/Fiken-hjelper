using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Budgeting;

/// <summary>
/// Summerer Fiken-posteringer per prosjekt og videre per budsjettpost.
/// </summary>
public static class BudgetReportBuilder
{
    /// <summary>
    /// Resultatkonti som teller med. 88xx/89xx (årsresultat og disponeringer) holdes utenfor,
    /// ellers ville årsavslutningen nullet ut resultatet.
    /// </summary>
    public const int FirstResultAccount = 3000;
    public const int LastResultAccount = 8799;

    public static BudgetYearReport Build(
        BudgetYear budget,
        IReadOnlyList<FikenProject> projects,
        IReadOnlyList<FikenJournalEntry>? journalEntries)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(projects);

        var projectNames = projects
            .GroupBy(p => p.ProjectId)
            .ToDictionary(g => g.Key, g => g.First().Name);

        var perProject = new Dictionary<long, decimal>();
        var unallocated = new List<UnallocatedPosting>();
        var severalProjects = 0;
        decimal fikenResult = 0;

        foreach (var entry in journalEntries ?? [])
        {
            foreach (var line in entry.Lines ?? [])
            {
                if (!TryParseAccount(line.Account, out var accountNumber)
                    || accountNumber is < FirstResultAccount or > LastResultAccount)
                {
                    continue;
                }

                // Fiken: debet positiv, kredit negativ. Resultatbidrag er derfor motsatt fortegn.
                var amount = -line.Amount / 100m;
                fikenResult += amount;

                var projectIds = line.ProjectId ?? [];
                if (projectIds.Count == 0)
                {
                    unallocated.Add(new UnallocatedPosting(
                        entry.JournalEntryId,
                        entry.JournalEntryNumber,
                        entry.Date,
                        entry.Description,
                        line.Account!,
                        amount));
                    continue;
                }

                if (projectIds.Count > 1)
                {
                    severalProjects++;
                }

                // Ved flere prosjekter på én linje brukes det første for å unngå dobbelttelling.
                var projectId = projectIds[0];
                perProject[projectId] = perProject.GetValueOrDefault(projectId) + amount;
            }
        }

        var hasFikenData = journalEntries is not null;
        var linkedCount = budget.Lines
            .SelectMany(l => l.ProjectIds.Distinct())
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        ProjectActual ToActual(long id) => new(
            id,
            projectNames.GetValueOrDefault(id) ?? $"Prosjekt {id}",
            perProject.GetValueOrDefault(id));

        var lines = budget.Lines.Select(line =>
        {
            var lineProjects = line.ProjectIds
                .Distinct()
                .Select(ToActual)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var isManual = line.ManualActual.HasValue;
            var actual = isManual ? line.ManualActual!.Value : lineProjects.Sum(p => p.Amount);
            return new BudgetLineReport(line.Name, line.Amount, actual, isManual, lineProjects);
        }).ToList();

        var unlinked = perProject
            .Where(kv => !linkedCount.ContainsKey(kv.Key) && kv.Value != 0)
            .Select(kv => ToActual(kv.Key))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var doubleLinked = linkedCount
            .Where(kv => kv.Value > 1)
            .Select(kv => ToActual(kv.Key))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new BudgetYearReport(
            budget.Year,
            lines,
            unallocated
                .OrderBy(p => p.Date, StringComparer.Ordinal)
                .ThenBy(p => p.JournalEntryNumber)
                .ToList(),
            unlinked,
            doubleLinked,
            severalProjects,
            hasFikenData)
        {
            FikenResult = hasFikenData ? fikenResult : null
        };
    }

    /// <summary>Tolker kontokoder som "3200" eller "2400:20079" (hovedkonto:underkonto).</summary>
    public static bool TryParseAccount(string? account, out int accountNumber)
    {
        accountNumber = 0;
        if (string.IsNullOrWhiteSpace(account))
        {
            return false;
        }

        var main = account.Split(':', 2)[0].Trim();
        return int.TryParse(main, out accountNumber);
    }
}

/// <summary>Stiller flere år ved siden av hverandre, koblet sammen på postnavn.</summary>
public sealed record BudgetComparison(
    IReadOnlyList<int> Years,
    IReadOnlyList<BudgetComparisonRow> Rows,
    IReadOnlyDictionary<int, BudgetYearReport> Reports)
{
    public static BudgetComparison Build(IEnumerable<BudgetYearReport> reports)
    {
        var ordered = reports.OrderByDescending(r => r.Year).ToList();
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var line in ordered.SelectMany(r => r.Lines))
        {
            if (seen.Add(line.Name))
            {
                names.Add(line.Name);
            }
        }

        var rows = names.Select(name => new BudgetComparisonRow(
            name,
            ordered.ToDictionary(
                r => r.Year,
                r => r.Lines.FirstOrDefault(l =>
                    string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase)))))
            .ToList();

        return new BudgetComparison(
            ordered.Select(r => r.Year).ToList(),
            rows,
            ordered.ToDictionary(r => r.Year));
    }
}

public sealed record BudgetComparisonRow(
    string Name,
    IReadOnlyDictionary<int, BudgetLineReport?> ByYear);
