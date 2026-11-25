using System.Globalization;

namespace FikenHjelper.Core.Budgeting;

public enum ProjectSplitProblem
{
    /// <summary>Prosjektet er koblet til flere poster, men minst én mangler andel.</summary>
    MissingShares,

    /// <summary>Alle andeler er satt, men summen er ikke 100 %.</summary>
    WrongTotal
}

/// <summary>Et prosjekt koblet til flere poster uten gyldig fordelingsnøkkel.</summary>
public sealed record ProjectSplitIssue(
    long ProjectId,
    string Name,
    decimal Amount,
    IReadOnlyList<string> Lines,
    decimal TotalShare,
    ProjectSplitProblem Problem)
{
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    public string Description => Problem == ProjectSplitProblem.MissingShares
        ? $"«{Name}» er koblet til {string.Join(", ", Lines.Select(l => $"«{l}»"))}, men mangler fordelingsnøkkel."
        : $"Fordelingen av «{Name}» på {string.Join(", ", Lines.Select(l => $"«{l}»"))} summerer til {TotalShare.ToString("0.##", Nb)} %, ikke 100 %.";
}

/// <summary>
/// Regler for prosjekter som er fordelt på flere budsjettposter. Bare poster som bruker Fiken-tall
/// (uten manuelt regnskapstall) teller som kobling.
/// </summary>
public static class ProjectSplits
{
    /// <summary>Hvor mye summen av andelene kan avvike fra 100 %.</summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>Postene hvert prosjekt er koblet til, i postrekkefølge. Poster med manuelt regnskapstall er ikke med.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<BudgetLine>> LinksByProject(BudgetYear year)
    {
        ArgumentNullException.ThrowIfNull(year);
        return year.Lines
            .Where(l => !l.ManualActual.HasValue)
            .SelectMany(l => l.ProjectIds.Distinct().Select(id => (Id: id, Line: l)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BudgetLine>)g.Select(x => x.Line).ToList());
    }

    /// <summary>Hva som er galt med fordelingen av prosjektet på <paramref name="lines"/>, eller null når den går opp.</summary>
    public static ProjectSplitProblem? Check(long projectId, IReadOnlyList<BudgetLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count <= 1)
        {
            return null;
        }

        if (lines.Any(l => l.ShareOf(projectId) is null))
        {
            return ProjectSplitProblem.MissingShares;
        }

        return Math.Abs(TotalShare(projectId, lines) - 100m) <= Tolerance ? null : ProjectSplitProblem.WrongTotal;
    }

    public static decimal TotalShare(long projectId, IEnumerable<BudgetLine> lines) =>
        lines.Sum(l => l.ShareOf(projectId) ?? 0m);

    /// <summary>
    /// Andelen (0–1) av prosjektet som telles i <paramref name="line"/>. Er prosjektet bare koblet til denne posten,
    /// eller posten har manuelt regnskapstall, teller hele prosjektet. Ellers brukes andelen, og hele prosjektet
    /// når andelen mangler (slik at en ufullstendig nøkkel synes som dobbelttelling og blir flagget).
    /// </summary>
    public static decimal Fraction(long projectId, BudgetLine line, IReadOnlyList<BudgetLine> links)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(links);
        return links.Count <= 1 || line.ManualActual.HasValue ? 1m : (line.ShareOf(projectId) ?? 100m) / 100m;
    }

    /// <summary>Andelen (0–1) per prosjekt i posten for året, til bruk i avviksforklaringer.</summary>
    public static IReadOnlyDictionary<long, decimal> LineFractions(BudgetYear year, BudgetLine line)
    {
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(line);
        var links = LinksByProject(year);
        return line.ProjectIds
            .Distinct()
            .ToDictionary(id => id, id => Fraction(id, line, links.GetValueOrDefault(id) ?? [line]));
    }

    /// <summary>
    /// Fordeler <paramref name="amount"/> på postene etter andelene. Med gyldig nøkkel avrundes hver del til øre,
    /// og den siste posten får resten, så summen alltid stemmer. Uten gyldig nøkkel får hver post sin andel
    /// (eller hele beløpet når andelen mangler).
    /// </summary>
    public static IReadOnlyList<decimal> Allocate(long projectId, decimal amount, IReadOnlyList<BudgetLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count <= 1)
        {
            return lines.Select(_ => amount).ToList();
        }

        var parts = lines
            .Select(l => Math.Round(amount * Fraction(projectId, l, lines), 2, MidpointRounding.AwayFromZero))
            .ToList();
        if (Check(projectId, lines) is null)
        {
            parts[^1] = amount - parts.Take(parts.Count - 1).Sum();
        }

        return parts;
    }

    /// <summary>Alle prosjekter i året som er koblet til flere poster uten gyldig nøkkel.</summary>
    public static IReadOnlyList<ProjectSplitIssue> Validate(
        BudgetYear year,
        Func<long, string> projectName,
        Func<long, decimal>? projectAmount = null)
    {
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(projectName);
        return LinksByProject(year)
            .Select(kv => (kv.Key, Lines: kv.Value, Problem: Check(kv.Key, kv.Value)))
            .Where(x => x.Problem is not null)
            .Select(x => new ProjectSplitIssue(
                x.Key,
                projectName(x.Key),
                projectAmount?.Invoke(x.Key) ?? 0m,
                x.Lines.Select(l => l.Name).ToList(),
                TotalShare(x.Key, x.Lines),
                x.Problem!.Value))
            .OrderBy(i => i.Name, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ToList();
    }
}
