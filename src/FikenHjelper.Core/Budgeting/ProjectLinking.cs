using System.Globalization;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Budgeting;

/// <summary>Gruppene i prosjektvelgeren, i den rekkefølgen de vises.</summary>
public enum ProjectPickerGroup
{
    Selected,
    Suggested,
    Unlinked,
    LinkedElsewhere,
    Completed
}

/// <summary>Ett prosjekt i velgeren for en budsjettpost.</summary>
public sealed record ProjectPickerOption(
    FikenProject Project,
    ProjectPickerGroup Group,
    ProjectActivity? Activity,
    string? UsedBy,
    NameMatch<FikenProject>? Match);

/// <summary>Ett prosjekt med posteringer i året, med postene det er koblet til og eventuelt et forslag.</summary>
public sealed record ProjectLinkRow(
    FikenProject Project,
    ProjectActivity Activity,
    IReadOnlyList<BudgetLine> Lines,
    NameMatch<BudgetLine>? Suggestion,
    bool SuggestionIsStrong);

/// <summary>Logikken bak koblingen mellom budsjettposter og Fiken-prosjekter i «Rediger budsjett».</summary>
public static class ProjectLinking
{
    /// <summary>Så mange forslag vises øverst i velgeren.</summary>
    public const int MaxSuggestions = 5;

    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");
    private static readonly StringComparer NameComparer = StringComparer.Create(Nb, ignoreCase: true);

    /// <summary>
    /// Prosjektene i velgeren for <paramref name="line"/>, gruppert og sortert:
    /// valgte, forslag etter navn, ukoblede, koblet til andre poster og avsluttede uten posteringer i året.
    /// De to siste gruppene vises bare når de er slått på, eller når brukeren søker.
    /// </summary>
    public static IReadOnlyList<ProjectPickerOption> BuildPickerOptions(
        BudgetLine line,
        IReadOnlyCollection<long> selected,
        BudgetYear year,
        BudgetYear? previousYear,
        IReadOnlyList<FikenProject> projects,
        IReadOnlyDictionary<long, ProjectActivity>? activity,
        string? search = null,
        bool showLinkedElsewhere = false,
        bool showCompleted = false)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(projects);

        var usedBy = UsedByOtherLines(year, line);
        var history = HistoryProjectIds(line.Name, previousYear);
        var searching = !string.IsNullOrWhiteSpace(search);

        var options = new List<ProjectPickerOption>();
        var unlinked = new List<FikenProject>();
        foreach (var project in projects.GroupBy(p => p.ProjectId).Select(g => g.First()))
        {
            if (searching && !Matches(project, search!))
            {
                continue;
            }

            var projectActivity = activity?.GetValueOrDefault(project.ProjectId);
            var other = usedBy.GetValueOrDefault(project.ProjectId);
            ProjectPickerGroup group;
            if (selected.Contains(project.ProjectId))
            {
                group = ProjectPickerGroup.Selected;
            }
            else if (other is not null)
            {
                group = ProjectPickerGroup.LinkedElsewhere;
            }
            else if (project.Completed && (projectActivity?.PostingCount ?? 0) == 0)
            {
                group = ProjectPickerGroup.Completed;
            }
            else
            {
                unlinked.Add(project);
                continue;
            }

            if (group == ProjectPickerGroup.Selected
                || searching
                || (group == ProjectPickerGroup.LinkedElsewhere && showLinkedElsewhere)
                || (group == ProjectPickerGroup.Completed && showCompleted))
            {
                options.Add(new ProjectPickerOption(project, group, projectActivity, other, null));
            }
        }

        var suggestions = ProjectNameMatcher
            .Rank(line.Name, unlinked, p => p.Name, p => history.Contains(p.ProjectId))
            .Take(MaxSuggestions)
            .ToList();
        var suggested = suggestions.Select(m => m.Item.ProjectId).ToHashSet();

        options.AddRange(suggestions.Select(m => new ProjectPickerOption(
            m.Item, ProjectPickerGroup.Suggested, activity?.GetValueOrDefault(m.Item.ProjectId), null, m)));
        options.AddRange(unlinked
            .Where(p => !suggested.Contains(p.ProjectId))
            .Select(p => new ProjectPickerOption(p, ProjectPickerGroup.Unlinked, activity?.GetValueOrDefault(p.ProjectId), null, null)));

        // Forslagene er allerede rangert. De andre gruppene sorteres på navn.
        return options
            .Select((o, i) => (Option: o, Index: i))
            .OrderBy(x => x.Option.Group)
            .ThenBy(x => x.Option.Group == ProjectPickerGroup.Suggested ? x.Index : 0)
            .ThenBy(x => x.Option.Project.Name, NameComparer)
            .Select(x => x.Option)
            .ToList();
    }

    /// <summary>
    /// Prosjektene med posteringer i året, med koblede poster og forslag til post for de ukoblede.
    /// Forslag kommer fra fjorårets kobling (post med samme navn) eller fra likhet mellom navnene.
    /// Poster med manuelt regnskapstall foreslås ikke, siden de ikke bruker Fiken-tall.
    /// </summary>
    public static IReadOnlyList<ProjectLinkRow> BuildProjectRows(
        BudgetYear year,
        BudgetYear? previousYear,
        IReadOnlyList<FikenProject> projects,
        IReadOnlyDictionary<long, ProjectActivity> activity)
    {
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(activity);

        var candidates = year.Lines.Where(l => !l.ManualActual.HasValue).ToList();
        return projects
            .GroupBy(p => p.ProjectId)
            .Select(g => g.First())
            .Where(p => activity.TryGetValue(p.ProjectId, out var a) && a.PostingCount > 0)
            .Select(project =>
            {
                var lines = year.Lines.Where(l => l.ProjectIds.Contains(project.ProjectId)).ToList();
                if (lines.Count > 0)
                {
                    return new ProjectLinkRow(project, activity[project.ProjectId], lines, null, false);
                }

                var historyNames = previousYear?.Lines
                    .Where(l => l.ProjectIds.Contains(project.ProjectId))
                    .Select(l => l.Name.Trim())
                    .ToHashSet(NameComparer) ?? [];
                var ranked = ProjectNameMatcher.Rank(project.Name, candidates, l => l.Name, l => historyNames.Contains(l.Name.Trim()));
                return new ProjectLinkRow(
                    project,
                    activity[project.ProjectId],
                    lines,
                    ranked.FirstOrDefault(),
                    ProjectNameMatcher.IsStrong(ranked));
            })
            .OrderBy(r => r.Lines.Count > 0)
            .ThenBy(r => r.Project.Name, NameComparer)
            .ToList();
    }

    /// <summary>Prosjektene som er koblet til andre poster enn <paramref name="line"/>, med postnavnene.</summary>
    public static IReadOnlyDictionary<long, string> UsedByOtherLines(BudgetYear year, BudgetLine line)
    {
        ArgumentNullException.ThrowIfNull(year);
        return year.Lines
            .Where(l => !ReferenceEquals(l, line))
            .SelectMany(l => l.ProjectIds.Distinct().Select(id => (Id: id, l.Name)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => $"«{x.Name}»")));
    }

    /// <summary>Prosjektene som var koblet til en post med samme navn året før.</summary>
    public static IReadOnlySet<long> HistoryProjectIds(string lineName, BudgetYear? previousYear) =>
        previousYear?.Lines
            .Where(l => NameComparer.Equals(l.Name.Trim(), lineName.Trim()))
            .SelectMany(l => l.ProjectIds)
            .ToHashSet() ?? [];

    private static bool Matches(FikenProject project, string search)
    {
        var term = search.Trim();
        return Nb.CompareInfo.IndexOf(project.Name, term, CompareOptions.IgnoreCase) >= 0
            || (project.Number is { } number && number.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
