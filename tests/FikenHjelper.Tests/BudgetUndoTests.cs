using FikenHjelper.Core.Budgeting;

namespace FikenHjelper.Tests;

public sealed class BudgetUndoTests
{
    private static BudgetData Data(params BudgetLine[] lines) =>
        new() { Years = [new BudgetYear { Year = 2026, Lines = [.. lines] }] };

    private static BudgetLine Line(string name, params long[] projects) =>
        new() { Name = name, ProjectIds = [.. projects] };

    [Fact]
    public void RestoreLine_InsertsAtOriginalIndex()
    {
        var removed = Line("Leir");
        var data = Data(Line("Kurs"), Line("Kontingent"));

        Assert.Null(BudgetUndo.RestoreLine(data, 2026, 1, removed, discardedSince: false));
        Assert.Equal(["Kurs", "Leir", "Kontingent"], data.Years[0].Lines.Select(l => l.Name));
    }

    [Fact]
    public void RestoreLine_RefusesAfterDiscard()
    {
        // Slettet uten å lagre, forkastet ved årsbytte: filen har fortsatt posten.
        var removed = Line("Leir");
        var data = Data(Line("Leir"), Line("Kurs"));

        Assert.Equal(BudgetUndo.DiscardedMessage, BudgetUndo.RestoreLine(data, 2026, 0, removed, discardedSince: true));
        Assert.Equal(2, data.Years[0].Lines.Count);
    }

    [Fact]
    public void RestoreLine_RefusesWhenNameAlreadyExists()
    {
        var removed = Line("Leir");
        var data = Data(Line(" leir "));

        var error = BudgetUndo.RestoreLine(data, 2026, 0, removed, discardedSince: false);

        Assert.NotNull(error);
        Assert.Contains("Leir", error);
        Assert.Single(data.Years[0].Lines);
    }

    [Fact]
    public void RestoreLine_RefusesWhenYearIsGone()
    {
        var data = new BudgetData();
        Assert.NotNull(BudgetUndo.RestoreLine(data, 2026, 0, Line("Leir"), discardedSince: false));
    }

    [Fact]
    public void RestoreProjectLink_FindsLineByNameAfterSaveReload()
    {
        // Etter lagring er budsjettet lastet på nytt, så posten er et nytt objekt med samme navn.
        var oldLine = Line("Leir");
        var reloaded = Line("Leir", 900002);
        var data = Data(reloaded);

        var error = BudgetUndo.RestoreProjectLink(data, 2026, oldLine, 900001, 0, 60m, [], discardedSince: false, "Landsleir");

        Assert.Null(error);
        Assert.Equal([900001L, 900002L], reloaded.ProjectIds);
        Assert.Equal(60m, reloaded.ShareOf(900001));
        Assert.Empty(oldLine.ProjectIds);
    }

    [Fact]
    public void RestoreProjectLink_RefusesWhenLinkedToAnotherLineMeanwhile()
    {
        var line = Line("Leir");
        var other = Line("Kurs", 900001);
        var data = Data(line, other);

        var error = BudgetUndo.RestoreProjectLink(data, 2026, line, 900001, 0, null, [], discardedSince: false, "Landsleir");

        Assert.NotNull(error);
        Assert.Contains("Kurs", error);
        Assert.Empty(line.ProjectIds);
    }

    [Fact]
    public void RestoreProjectLink_AllowsLinesItWasAlreadySplitWith()
    {
        var line = Line("Leir");
        var other = Line("Kurs", 900001);
        var data = Data(line, other);

        var error = BudgetUndo.RestoreProjectLink(data, 2026, line, 900001, 0, 50m, ["Kurs"], discardedSince: false, "Landsleir");

        Assert.Null(error);
        Assert.Contains(900001L, line.ProjectIds);
    }

    [Fact]
    public void RestoreProjectLink_RefusesWhenLineIsGoneOrAlreadyLinked()
    {
        var gone = BudgetUndo.RestoreProjectLink(Data(Line("Kurs")), 2026, Line("Leir"), 900001, 0, null, [], false, "Landsleir");
        Assert.NotNull(gone);
        Assert.Contains("finnes ikke lenger", gone);

        var linked = Line("Leir", 900001);
        var already = BudgetUndo.RestoreProjectLink(Data(linked), 2026, linked, 900001, 0, null, [], false, "Landsleir");
        Assert.NotNull(already);
        Assert.Single(linked.ProjectIds);
    }

    [Fact]
    public void RestoreProjectLink_RefusesAfterDiscard()
    {
        var line = Line("Leir");
        Assert.Equal(
            BudgetUndo.DiscardedMessage,
            BudgetUndo.RestoreProjectLink(Data(line), 2026, line, 900001, 0, null, [], discardedSince: true, "Landsleir"));
    }
}
