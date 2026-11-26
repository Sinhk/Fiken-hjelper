using System.Globalization;

namespace FikenHjelper.Core.Grants;

/// <summary>Et aktivt tilskudd som et Fiken-prosjekt er koblet til. <paramref name="SharePercent"/> er andelen av prosjektets kostnader.</summary>
public sealed record GrantProjectLink(Guid GrantId, string Name, decimal SharePercent)
{
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    public string Href => $"tilskudd/{GrantId}";

    public string Tooltip => $"Tilskudd: {Name} ({SharePercent.ToString("0.##", Nb)} %)";
}

/// <summary>Oppslag fra Fiken-prosjekt til tilskuddene det er finansiert av, brukt til merking på budsjettsiden. Bare lesing.</summary>
public static class GrantProjectLinks
{
    /// <summary>
    /// Aktive prosjekttilskudd (ikke avslått eller trukket) per prosjekt. Mangler andel, regnes hele prosjektet (100 %),
    /// som i beregningen av «brukt».
    /// </summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<GrantProjectLink>> Build(IEnumerable<Grant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        return grants
            .Where(g => g.IsActive && g.Kind == GrantKind.Project)
            .SelectMany(g => (g.ProjectIds ?? []).Distinct().Select(id => (Id: id, Link: new GrantProjectLink(g.Id, g.DisplayName, g.ShareOf(id) ?? 100m))))
            .GroupBy(x => x.Id)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<GrantProjectLink>)g.Select(x => x.Link).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }
}
