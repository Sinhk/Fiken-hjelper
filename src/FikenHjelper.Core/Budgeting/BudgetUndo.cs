namespace FikenHjelper.Core.Budgeting;

/// <summary>
/// Angre for slettinger i budsjettoppsettet. Budsjettet kan være lastet på nytt fra filen mellom slettingen og
/// «Angre» (etter lagring eller når ulagrede endringer forkastes), så postene slås opp på nytt i stedet for å
/// bruke gamle objekter. Metodene returnerer null når angringen ble utført, ellers en feilmelding til brukeren.
/// </summary>
public static class BudgetUndo
{
    public const string DiscardedMessage =
        "Kunne ikke angre fordi ulagrede endringer er forkastet og budsjettet er lastet inn på nytt.";

    /// <summary>
    /// Setter en slettet post tilbake på plassen den hadde. Avvises når budsjettet er lastet på nytt med
    /// forkastede endringer siden slettingen, eller når året allerede har en post med samme navn.
    /// </summary>
    public static string? RestoreLine(BudgetData data, int year, int index, BudgetLine line, bool discardedSince)
    {
        if (discardedSince)
        {
            return DiscardedMessage;
        }

        var target = data.FindYear(year);
        if (target is null)
        {
            return $"Kunne ikke angre. Budsjettet for {year} finnes ikke lenger.";
        }

        if (target.Lines.Contains(line))
        {
            return $"Kunne ikke angre. Posten «{line.Name}» finnes allerede i budsjettet for {year}.";
        }

        if (FindByName(target, line.Name) is not null)
        {
            return $"Kunne ikke angre. Det finnes allerede en post som heter «{line.Name.Trim()}» i budsjettet for {year}.";
        }

        target.Lines.Insert(Math.Clamp(index, 0, target.Lines.Count), line);
        return null;
    }

    /// <summary>
    /// Kobler prosjektet til posten igjen etter «Fjern prosjekt». Posten finnes først som samme objekt, ellers
    /// på navn (etter lagring er budsjettet lastet på nytt). Avvises når prosjektet i mellomtiden er koblet til
    /// en annen post enn dem det var koblet til da det ble fjernet.
    /// </summary>
    /// <param name="otherLinesAtRemoval">Navnene på de andre postene prosjektet var koblet til da det ble fjernet.</param>
    public static string? RestoreProjectLink(
        BudgetData data,
        int year,
        BudgetLine line,
        long projectId,
        int index,
        decimal? share,
        IReadOnlyCollection<string> otherLinesAtRemoval,
        bool discardedSince,
        string projectName)
    {
        if (discardedSince)
        {
            return DiscardedMessage;
        }

        var yearData = data.FindYear(year);
        var target = yearData is null
            ? null
            : yearData.Lines.FirstOrDefault(l => ReferenceEquals(l, line)) ?? FindByName(yearData, line.Name);
        if (yearData is null || target is null)
        {
            return $"Kunne ikke angre. Posten «{line.Name}» finnes ikke lenger i budsjettet for {year}.";
        }

        if (target.ProjectIds.Contains(projectId))
        {
            return $"Kunne ikke angre. «{projectName}» er allerede koblet til «{target.Name}».";
        }

        var newOther = yearData.Lines
            .Where(l => !ReferenceEquals(l, target) && l.ProjectIds.Contains(projectId))
            .FirstOrDefault(l => !otherLinesAtRemoval.Contains(l.Name.Trim(), StringComparer.CurrentCultureIgnoreCase));
        if (newOther is not null)
        {
            return $"Kunne ikke angre. «{projectName}» er nå koblet til «{newOther.Name}». Koble prosjektet på nytt om du vil flytte det.";
        }

        target.ProjectIds.Insert(Math.Clamp(index, 0, target.ProjectIds.Count), projectId);
        target.SetShare(projectId, share);
        return null;
    }

    private static BudgetLine? FindByName(BudgetYear year, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : year.Lines.FirstOrDefault(l => string.Equals(l.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
}
