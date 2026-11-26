using ClosedXML.Excel;
using FikenHjelper.Core.Analysis;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class ProjectSplitTests
{
    private static readonly FikenProject Leir = new(900001, "1", "Landsleir", null, null, null, false);
    private static readonly FikenProject Kurs = new(900002, "2", "Kurs", null, null, null, false);

    private static FikenJournalEntry Entry(long id, string account, long amountOre, long project) =>
        new(id, id, "Fri", (int)id, $"Bilag {id}", "2026-03-01",
            [new FikenJournalEntryLine(amountOre, account, null, [project]), new FikenJournalEntryLine(-amountOre, "1920", null, [])]);

    private static BudgetLine Line(string name, params (long Id, decimal? Share)[] links)
    {
        var line = new BudgetLine { Name = name, ProjectIds = links.Select(l => l.Id).ToList() };
        foreach (var (id, share) in links)
        {
            line.SetShare(id, share);
        }

        return line;
    }

    [Theory]
    [InlineData]
    [InlineData(100)]
    public void DescribeShareProblem_IsNull_ForZeroOrOneLine(params int[] shares) =>
        Assert.Null(ProjectSplits.DescribeShareProblem([.. shares.Select(s => (decimal?)s)]));

    [Fact]
    public void DescribeShareProblem_IsNull_WhenSharesSumTo100() =>
        Assert.Null(ProjectSplits.DescribeShareProblem([33.33m, 33.33m, 33.34m]));

    [Fact]
    public void DescribeShareProblem_ReportsTotal_WhenNot100() =>
        Assert.Equal("Andelene summerer til 90 %. De må bli 100 %.", ProjectSplits.DescribeShareProblem([60m, 30m]));

    [Fact]
    public void DescribeShareProblem_ReportsMissingOrZeroShare()
    {
        const string expected = "Fyll inn en andel større enn 0 for alle postene.";
        Assert.Equal(expected, ProjectSplits.DescribeShareProblem([100m, null]));
        Assert.Equal(expected, ProjectSplits.DescribeShareProblem([100m, 0m]));
    }

    [Fact]
    public void Build_SplitsProjectResultByShares_AndSumsExactly()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                Line("A", (Leir.ProjectId, 33.33m)),
                Line("B", (Leir.ProjectId, 33.33m)),
                Line("C", (Leir.ProjectId, 33.34m))
            ]
        };

        // Kostnad 1 000,01 kr på leiren.
        var report = BudgetReportBuilder.Build(budget, [Leir], [Entry(1, "6800", 100_001, Leir.ProjectId)]);

        Assert.Equal([-333.30m, -333.30m, -333.41m], report.Lines.Select(l => l.Actual));
        Assert.Equal(-1000.01m, report.TotalActual);
        Assert.Equal(report.FikenResult, report.TotalActual);
        Assert.Empty(report.InvalidProjectSplits);

        var project = Assert.Single(report.Lines[0].Projects);
        Assert.Equal(33.33m, project.Share);
        Assert.Equal(-1000.01m, project.FullAmount);
    }

    [Fact]
    public void Build_SingleLinkCountsWholeProject_EvenWithStaleShare()
    {
        var budget = new BudgetYear { Year = 2026, Lines = [Line("A", (Leir.ProjectId, 40m))] };

        var report = BudgetReportBuilder.Build(budget, [Leir], [Entry(1, "3200", -100_000, Leir.ProjectId)]);

        Assert.Equal(1000m, report.Lines[0].Actual);
        Assert.Null(report.Lines[0].Projects[0].FullAmount);
        Assert.Empty(report.InvalidProjectSplits);
    }

    [Fact]
    public void Build_FlagsWrongTotal_AndMissingShares()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                Line("A", (Leir.ProjectId, 40m), (Kurs.ProjectId, 50m)),
                Line("B", (Leir.ProjectId, 50m), (Kurs.ProjectId, null))
            ]
        };

        var report = BudgetReportBuilder.Build(budget, [Leir, Kurs],
            [Entry(1, "6800", 100_000, Leir.ProjectId), Entry(2, "6800", 100_000, Kurs.ProjectId)]);

        var kurs = report.InvalidProjectSplits.Single(i => i.ProjectId == Kurs.ProjectId);
        Assert.Equal(ProjectSplitProblem.MissingShares, kurs.Problem);
        var leir = report.InvalidProjectSplits.Single(i => i.ProjectId == Leir.ProjectId);
        Assert.Equal(ProjectSplitProblem.WrongTotal, leir.Problem);
        Assert.Equal(90m, leir.TotalShare);
        Assert.Equal(-1000m, leir.Amount);
        Assert.Contains("90 %", leir.Description);

        // Uten gyldig nøkkel teller hver post sin andel, eller hele prosjektet når andelen mangler.
        Assert.Equal(-400m - 500m, report.Lines[0].Actual);
        Assert.Equal(-500m - 1000m, report.Lines[1].Actual);

        Assert.Contains(AnomalyFinder.FromBudgetReport(report), a => a.Type == AnomalyTypes.InvalidProjectSplits);
    }

    [Fact]
    public void Build_ManualLinesDoNotTakePartInSplit()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                new BudgetLine { Name = "Manuell", ManualActual = 5m, ProjectIds = [Leir.ProjectId] },
                Line("A", (Leir.ProjectId, null))
            ]
        };

        var report = BudgetReportBuilder.Build(budget, [Leir], [Entry(1, "3200", -100_000, Leir.ProjectId)]);

        Assert.Empty(report.InvalidProjectSplits);
        Assert.Equal(1000m, report.Lines[1].Actual);
    }

    [Fact]
    public void LineFractions_GivesShareForSplitProjectsOnly()
    {
        var a = Line("A", (Leir.ProjectId, 25m), (Kurs.ProjectId, 60m));
        var b = Line("B", (Leir.ProjectId, 75m));
        var year = new BudgetYear { Year = 2026, Lines = [a, b] };

        var fractions = ProjectSplits.LineFractions(year, a);

        Assert.Equal(0.25m, fractions[Leir.ProjectId]);
        Assert.Equal(1m, fractions[Kurs.ProjectId]);
    }

    [Fact]
    public void Explain_WeightsAmountsByFraction()
    {
        FikenJournalEntry[] entries = [Entry(1, "6800", 100_000, Leir.ProjectId), Entry(2, "6900", 50_000, Leir.ProjectId)];

        var explanation = ProjectVarianceExplainer.Explain(
            2026, new Dictionary<long, decimal> { [Leir.ProjectId] = 0.4m }, entries, null, [], 10);

        Assert.Equal(-600m, explanation.Actual);
        Assert.Equal(-400m, explanation.ByAccount.Single(a => a.Account == "6800").Amount);
        Assert.All(explanation.LargestPostings, p => Assert.Equal(40m, p.Share));
    }

    [Fact]
    public void Export_ProjectsSheetShowsShareAndProjectTotal()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines = [Line("A", (Leir.ProjectId, 40m)), Line("B", (Leir.ProjectId, 60m))]
        };
        var report = BudgetReportBuilder.Build(budget, [Leir], [Entry(1, "3200", -100_000, Leir.ProjectId)]);

        var bytes = BudgetWorkbookExporter.Export(BudgetComparison.Build([report]));

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet("Prosjekter");
        Assert.Equal("Andel", ws.Cell(1, 5).GetString());
        Assert.Equal(400m, (decimal)ws.Cell(2, 4).GetDouble());
        Assert.Equal(40m, (decimal)ws.Cell(2, 5).GetDouble());
        Assert.Equal(1000m, (decimal)ws.Cell(2, 6).GetDouble());
    }

    [Theory]
    [InlineData(new[] { -400.0, -600.0 }, new[] { 40.0, 60.0 })]
    [InlineData(new[] { 1.0, 1.0, 1.0 }, new[] { 33.33, 33.33, 33.34 })]
    public void SharesFromAmounts_GivesPercentSummingTo100(double[] amounts, double[] expected)
    {
        var shares = BudgetWorkbookImporter.SharesFromAmounts(amounts.Select(a => (decimal?)TestAmounts.Exact(a)).ToList());

        Assert.Equal(expected.Select(TestAmounts.Exact), shares!);
        Assert.Equal(100m, shares!.Sum());
    }

    [Fact]
    public void SharesFromAmounts_RefusesMissingZeroOrMixedSigns()
    {
        Assert.Null(BudgetWorkbookImporter.SharesFromAmounts([100m, null]));
        Assert.Null(BudgetWorkbookImporter.SharesFromAmounts([100m, 0m]));
        Assert.Null(BudgetWorkbookImporter.SharesFromAmounts([100m, -50m]));

        // 4 kr ved siden av 100 000 kr blir 0 % etter avrunding, og det er ikke en gyldig andel.
        Assert.Null(BudgetWorkbookImporter.SharesFromAmounts([100_000m, 4m]));
    }

    [Fact]
    public void ApplySuggestions_SplitsSuggestedProjectOnSeveralLinesFromAmounts()
    {
        using var wb = new XLWorkbook();
        var overview = wb.AddWorksheet("Oversikt");
        overview.Cell("B1").Value = "Budsjett 2026";
        overview.Cell("A2").Value = "Leir";
        overview.Cell("B2").Value = 1000;
        overview.Cell("A3").Value = "Kurs";
        overview.Cell("B3").Value = 500;

        var sheet = wb.AddWorksheet("Regnskap 2026");
        sheet.Cell("A1").Value = "Prosjekt";
        sheet.Cell("B1").Value = "Sum";
        sheet.Cell("C1").Value = "Budsjettpost";
        sheet.Cell("A2").Value = "Landsleirr";
        sheet.Cell("B2").Value = 20000;
        sheet.Cell("C2").Value = "Leir";
        sheet.Cell("A3").Value = "Landsleirr";
        sheet.Cell("B3").Value = 10000;
        sheet.Cell("C3").Value = "Leir";
        sheet.Cell("A4").Value = "Landsleirr";
        sheet.Cell("B4").Value = 10000;
        sheet.Cell("C4").Value = "Kurs";

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var result = BudgetWorkbookImporter.Import(stream, [Leir, Kurs]);
        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(30000m, suggestion.Targets.Single(t => t.LineName == "Leir").Amount);

        var years = result.Years.Select(y => y.Copy()).ToList();
        var messages = BudgetWorkbookImporter.ApplySuggestions(years, [(suggestion, Leir.ProjectId)]);

        var lines = years.Single().Lines;
        Assert.Equal(75m, lines[0].ShareOf(Leir.ProjectId));
        Assert.Equal(25m, lines[1].ShareOf(Leir.ProjectId));
        Assert.All(lines, l => Assert.Contains(Leir.ProjectId, l.ProjectIds));
        Assert.Contains(messages, m => m.Contains("75 %"));
    }

    [Fact]
    public void Import_ProjectOnSeveralLinesGetsSharesFromSumColumn()
    {
        using var wb = new XLWorkbook();
        var overview = wb.AddWorksheet("Oversikt");
        overview.Cell("B1").Value = "Budsjett 2026";
        overview.Cell("A2").Value = "Leirer";
        overview.Cell("B2").Value = 1000;
        overview.Cell("A3").Value = "Kurs";
        overview.Cell("B3").Value = 500;
        overview.Cell("A4").Value = "Drift";
        overview.Cell("B4").Value = -100;

        var sheet = wb.AddWorksheet("Regnskap 2026");
        sheet.Cell("A1").Value = "Prosjekt";
        sheet.Cell("B1").Value = "Sum";
        sheet.Cell("C1").Value = "Budsjettpost";
        sheet.Cell("A2").Value = "Landsleir";
        sheet.Cell("B2").Value = 300;
        sheet.Cell("C2").Value = "Leirer";
        sheet.Cell("A3").Value = "Landsleir";
        sheet.Cell("B3").Value = 100;
        sheet.Cell("C3").Value = "Kurs";
        sheet.Cell("A4").Value = "Landsleir";
        sheet.Cell("B4").Value = 100;
        sheet.Cell("C4").Value = "Kurs";
        sheet.Cell("A5").Value = "Kurs";
        sheet.Cell("C5").Value = "Kurs";
        sheet.Cell("A6").Value = "Kurs";
        sheet.Cell("C6").Value = "Drift";

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;

        var result = BudgetWorkbookImporter.Import(stream, [Leir, Kurs]);

        var lines = result.Years.Single().Lines;
        Assert.Equal(60m, lines[0].ShareOf(Leir.ProjectId));
        Assert.Equal(40m, lines[1].ShareOf(Leir.ProjectId));
        Assert.Contains(result.Warnings, w => w.Contains("Landsleir") && w.Contains("60 %"));

        // Uten beløp blir det ingen nøkkel, bare en advarsel.
        Assert.Null(lines[1].ShareOf(Kurs.ProjectId));
        Assert.Contains(result.Warnings, w => w.Contains("«Kurs»") && w.Contains("ingen fordelingsnøkkel"));
    }

    [Fact]
    public async Task Store_KeepsSharesForLinkedProjectsOnly_AndReadsOldFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fh-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            // Fil i gammelt format, uten projectShares.
            await File.WriteAllTextAsync(
                Path.Combine(dir, "budgets.json"),
                """{"years":[{"year":2026,"lines":[{"name":"A","amount":1,"projectIds":[900001]}]}]}""",
                TestContext.Current.CancellationToken);
            var store = new JsonBudgetStore(dir);

            var old = await store.LoadAsync(TestContext.Current.CancellationToken);
            var line = Assert.Single(old.Years.Single().Lines);
            Assert.Equal([900001L], line.ProjectIds);
            Assert.Null(line.ProjectShares);

            line.ProjectIds.Add(900002);
            line.SetShare(900001, 40m);
            line.SetShare(900002, 150m);
            line.SetShare(900003, 10m);
            await store.SaveAsync(old, TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(Path.Combine(dir, "budgets.json"), TestContext.Current.CancellationToken);
            Assert.Contains("\"projectIds\"", json);
            Assert.Contains("\"projectShares\"", json);

            var loaded = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken)).Years.Single().Lines);
            Assert.Equal(new Dictionary<long, decimal> { [900001] = 40m }, loaded.ProjectShares);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
