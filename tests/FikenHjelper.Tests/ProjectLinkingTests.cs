using ClosedXML.Excel;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class ProjectNameMatcherTests
{
    [Theory]
    [InlineData("  Landsleir (2026) ", "landsleir 2026")]
    [InlineData("Kryss-og-tvers", "kryss og tvers")]
    [InlineData("ØVELSE Å", "øvelse å")]
    [InlineData("--", "--")]
    public void Normalize_LowercasesAndStripsPunctuation_KeepsYearsAndNorwegianLetters(string input, string expected)
    {
        Assert.Equal(expected, ProjectNameMatcher.Normalize(input));
    }

    [Fact]
    public void Score_RenamedProjectIsSuggestedButNotStrong()
    {
        var score = ProjectNameMatcher.Score("Kretsbannerkonkurranse", "Kretskonkurranse");

        Assert.InRange(score, ProjectNameMatcher.SuggestionThreshold, ProjectNameMatcher.StrongThreshold - 1);
    }

    [Theory]
    [InlineData("Landsleir", "landsleir", 100)]
    [InlineData("Landsleir 2026", "2026 Landsleir", 95)]
    [InlineData("Kurs", "Lederkurs", 90)]
    public void Score_HandlesCaseWordOrderAndContainment(string a, string b, int expected)
    {
        Assert.Equal(expected, ProjectNameMatcher.Score(a, b));
    }

    [Fact]
    public void Score_DifferentYearsAreNotTheSameName()
    {
        Assert.True(ProjectNameMatcher.Score("Landsleir 2025", "Landsleir 2026") < 100);
    }

    [Fact]
    public void Score_EmptyNamesGiveZero()
    {
        Assert.Equal(0, ProjectNameMatcher.Score("", "Drift"));
        Assert.Equal(0, ProjectNameMatcher.Score("Drift", null));
    }

    [Fact]
    public void Rank_OrdersExactThenHistoryThenScore_AndDropsWeakMatches()
    {
        string[] names = ["Administrasjon", "Lederkurs", "Kurs", "Patruljekurs 2025", "Speiderkurs"];

        var ranked = ProjectNameMatcher.Rank("Kurs", names, n => n, n => n == "Patruljekurs 2025");

        Assert.Equal("Kurs", ranked[0].Item);
        Assert.Equal(NameMatchReason.Exact, ranked[0].Reason);
        Assert.Equal("Patruljekurs 2025", ranked[1].Item);
        Assert.Equal(NameMatchReason.History, ranked[1].Reason);
        Assert.DoesNotContain(ranked, m => m.Item == "Administrasjon");
    }

    [Fact]
    public void IsStrong_RequiresHighScoreAndClearMargin()
    {
        static NameMatch<string> M(int score, NameMatchReason reason = NameMatchReason.Similar) => new("x", score, reason);

        Assert.True(ProjectNameMatcher.IsStrong([M(95), M(80)]));
        Assert.False(ProjectNameMatcher.IsStrong([M(95), M(92)]));
        Assert.False(ProjectNameMatcher.IsStrong([M(90)]));
        Assert.True(ProjectNameMatcher.IsStrong([M(100, NameMatchReason.Exact), M(99)]));
        Assert.False(ProjectNameMatcher.IsStrong([M(100, NameMatchReason.Exact), M(100, NameMatchReason.Exact)]));
        Assert.True(ProjectNameMatcher.IsStrong([M(40, NameMatchReason.History)]));
        Assert.False(ProjectNameMatcher.IsStrong<string>([]));
    }
}

public sealed class ProjectLinkingTests
{
    private static FikenProject P(long id, string name, bool completed = false) => new(id, id.ToString(), name, null, null, null, completed);

    private static readonly FikenProject Konkurranse = P(900001, "Kretskonkurranse");
    private static readonly FikenProject Leir = P(900002, "Landsleir");
    private static readonly FikenProject Kurs = P(900003, "Lederkurs");
    private static readonly FikenProject GammelLeir = P(900004, "Kretsleir 2019", completed: true);
    private static readonly FikenProject AvsluttetMedPosteringer = P(900005, "Høsttur", completed: true);
    private static readonly FikenProject Drift = P(900006, "Drift");

    private static readonly FikenProject[] All = [Konkurranse, Leir, Kurs, GammelLeir, AvsluttetMedPosteringer, Drift];

    private static readonly Dictionary<long, ProjectActivity> Activity = new()
    {
        [Konkurranse.ProjectId] = new(-8_450m, 3),
        [Leir.ProjectId] = new(12_000m, 10),
        [AvsluttetMedPosteringer.ProjectId] = new(-500m, 1),
        [Drift.ProjectId] = new(-1_000m, 2),
    };

    [Fact]
    public void BuildPickerOptions_GroupsSelectedSuggestedUnlinked_AndHidesLinkedAndCompletedWithoutPostings()
    {
        var target = new BudgetLine { Name = "Kretsbannerkonkurranse", ProjectIds = [Drift.ProjectId] };
        var other = new BudgetLine { Name = "Leirer", ProjectIds = [Leir.ProjectId] };
        var year = new BudgetYear { Year = 2026, Lines = [target, other] };

        var options = ProjectLinking.BuildPickerOptions(target, target.ProjectIds, year, null, All, Activity);

        Assert.Equal(ProjectPickerGroup.Selected, options[0].Group);
        Assert.Equal(Drift, options[0].Project);
        Assert.Equal(ProjectPickerGroup.Suggested, options[1].Group);
        Assert.Equal(Konkurranse, options[1].Project);
        Assert.DoesNotContain(options, o => o.Project == Leir);
        Assert.DoesNotContain(options, o => o.Project == GammelLeir);
        Assert.Contains(options, o => o.Project == AvsluttetMedPosteringer && o.Group == ProjectPickerGroup.Unlinked);
        Assert.Equal(-8_450m, options[1].Activity!.Amount);
    }

    [Fact]
    public void BuildPickerOptions_TogglesAndSearchShowHiddenProjects()
    {
        var target = new BudgetLine { Name = "Kurs" };
        var other = new BudgetLine { Name = "Leirer", ProjectIds = [Leir.ProjectId] };
        var year = new BudgetYear { Year = 2026, Lines = [target, other] };

        var all = ProjectLinking.BuildPickerOptions(target, [], year, null, All, Activity, showLinkedElsewhere: true, showCompleted: true);
        Assert.Contains(all, o => o.Project == Leir && o.Group == ProjectPickerGroup.LinkedElsewhere && o.UsedBy == "«Leirer»");
        Assert.Contains(all, o => o.Project == GammelLeir && o.Group == ProjectPickerGroup.Completed);

        var search = ProjectLinking.BuildPickerOptions(target, [], year, null, All, Activity, search: "leir");
        Assert.Equal([Leir, GammelLeir], search.Select(o => o.Project).OrderBy(p => p.ProjectId).ToArray());
    }

    [Fact]
    public void BuildPickerOptions_SuggestsLastYearsProjectsForLineWithSameName()
    {
        var target = new BudgetLine { Name = "Arrangementer" };
        var year = new BudgetYear { Year = 2026, Lines = [target] };
        var previous = new BudgetYear
        {
            Year = 2025,
            Lines = [new BudgetLine { Name = "arrangementer", ProjectIds = [Konkurranse.ProjectId] }]
        };

        var options = ProjectLinking.BuildPickerOptions(target, [], year, previous, All, Activity);

        var first = Assert.Single(options, o => o.Group == ProjectPickerGroup.Suggested);
        Assert.Equal(Konkurranse, first.Project);
        Assert.Equal(NameMatchReason.History, first.Match!.Reason);
    }

    [Fact]
    public void BuildPickerOptions_WithoutActivityHidesAllCompleted()
    {
        var target = new BudgetLine { Name = "Turer" };
        var year = new BudgetYear { Year = 2026, Lines = [target] };

        var options = ProjectLinking.BuildPickerOptions(target, [], year, null, All, activity: null);

        Assert.DoesNotContain(options, o => o.Project.Completed);
        Assert.All(options, o => Assert.Null(o.Activity));
    }

    [Fact]
    public void BuildProjectRows_ListsProjectsWithPostings_UnlinkedFirst_WithSuggestions()
    {
        var konkurranse = new BudgetLine { Name = "Kretsbannerkonkurranse" };
        var leirer = new BudgetLine { Name = "Leirer", ProjectIds = [Leir.ProjectId] };
        var manuell = new BudgetLine { Name = "Drift", ManualActual = 0 };
        var year = new BudgetYear { Year = 2026, Lines = [konkurranse, leirer, manuell] };

        var rows = ProjectLinking.BuildProjectRows(year, null, All, Activity);

        Assert.Equal(
            [Drift, AvsluttetMedPosteringer, Konkurranse, Leir],
            rows.Select(r => r.Project).ToArray());
        Assert.DoesNotContain(rows, r => r.Project == Kurs);

        var row = rows.Single(r => r.Project == Konkurranse);
        Assert.Same(konkurranse, row.Suggestion!.Item);
        Assert.False(row.SuggestionIsStrong);

        // Poster med manuelt regnskapstall foreslås ikke, selv med samme navn.
        Assert.Null(rows.Single(r => r.Project == Drift).Suggestion);
        Assert.Same(leirer, Assert.Single(rows.Single(r => r.Project == Leir).Lines));
    }

    [Fact]
    public void BuildProjectRows_LastYearsLinkIsAStrongSuggestion()
    {
        var arrangementer = new BudgetLine { Name = "Arrangementer" };
        var year = new BudgetYear { Year = 2026, Lines = [arrangementer, new BudgetLine { Name = "Kretsbannerkonkurranse" }] };
        var previous = new BudgetYear
        {
            Year = 2025,
            Lines = [new BudgetLine { Name = "Arrangementer", ProjectIds = [Konkurranse.ProjectId] }]
        };

        var row = ProjectLinking.BuildProjectRows(year, previous, All, Activity).Single(r => r.Project == Konkurranse);

        Assert.Same(arrangementer, row.Suggestion!.Item);
        Assert.Equal(NameMatchReason.History, row.Suggestion.Reason);
        Assert.True(row.SuggestionIsStrong);
    }

    [Fact]
    public void SumByProject_CountsResultPostingsPerPrimaryProject()
    {
        static FikenJournalEntryLine Line(string account, long ore, params long[] projects) => new(ore, account, null, projects);

        FikenJournalEntry[] entries =
        [
            new(1, 1, "Fri", 1, "Bilag 1", "2026-03-01", [Line("6800", 150_000, 900001), Line("1920", -150_000)]),
            new(2, 2, "Fri", 2, "Bilag 2", "2026-03-02", [Line("3200", -50_000, 900001, 900002), Line("1500", 50_000, 900002)]),
            new(3, 3, "Fri", 3, "Bilag 3", "2026-12-31", [Line("8800", 10_000, 900003)]),
        ];

        var activity = BudgetReportBuilder.SumByProject(entries);

        Assert.Equal(new ProjectActivity(-1_000m, 2), activity[900001]);
        Assert.False(activity.ContainsKey(900002));
        Assert.False(activity.ContainsKey(900003));
    }
}

public sealed class BudgetImportSuggestionTests
{
    private static MemoryStream CreateWorkbook()
    {
        using var wb = new XLWorkbook();
        var overview = wb.AddWorksheet("Oversikt");
        overview.Cell("B1").Value = "Budsjett 2026";
        overview.Cell("C1").Value = "Budsjett 2025";
        overview.Cell("A2").Value = "Kretsbannerkonkurranse";
        overview.Cell("B2").Value = -2000;
        overview.Cell("C2").Value = -1500;
        overview.Cell("A3").Value = "Leirer";
        overview.Cell("B3").Value = 5000;
        overview.Cell("C3").Value = 4000;

        foreach (var year in new[] { 2026, 2025 })
        {
            var sheet = wb.AddWorksheet($"Regnskap {year}");
            sheet.Cell("A1").Value = "Prosjekt";
            sheet.Cell("C1").Value = "Budsjettpost";
            sheet.Cell("A2").Value = "Kretsbannerkonkurranse";
            sheet.Cell("C2").Value = "Kretsbannerkonkurranse";
            sheet.Cell("A3").Value = "Landsleirr";
            sheet.Cell("C3").Value = "Leirer";
            sheet.Cell("A4").Value = "Noe helt annet";
            sheet.Cell("C4").Value = "Leirer";
        }

        var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static readonly FikenProject[] Fiken =
    [
        new(900001, "1", "Kretskonkurranse", null, null, null, false),
        new(900002, "2", "Landsleir", null, null, null, false),
        new(900003, "3", "Administrasjon", null, null, null, false),
    ];

    [Fact]
    public void Import_SuggestsClosestProjectOncePerName_WithoutLinkingIt()
    {
        using var stream = CreateWorkbook();
        var result = BudgetWorkbookImporter.Import(stream, Fiken);

        Assert.Equal(2, result.Suggestions.Count);

        var renamed = result.Suggestions.Single(s => s.SheetName == "Kretsbannerkonkurranse");
        Assert.Equal(900001, renamed.Candidates[0].Item.ProjectId);
        Assert.False(renamed.IsStrong);
        Assert.Equal([2026, 2025], renamed.Targets.Select(t => t.Year));

        var typo = result.Suggestions.Single(s => s.SheetName == "Landsleirr");
        Assert.Equal(900002, typo.Candidates[0].Item.ProjectId);
        Assert.True(typo.IsStrong);

        Assert.All(result.Years.SelectMany(y => y.Lines), l => Assert.Empty(l.ProjectIds));
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("Noe helt annet")));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Kretsbannerkonkurranse"));
    }

    [Fact]
    public void ApplySuggestions_LinksChosenProjectToAllTargets()
    {
        using var stream = CreateWorkbook();
        var result = BudgetWorkbookImporter.Import(stream, Fiken);
        var renamed = result.Suggestions.Single(s => s.SheetName == "Kretsbannerkonkurranse");

        BudgetWorkbookImporter.ApplySuggestions(result, [(renamed, 900001L), (renamed, 900001L)]);

        foreach (var year in result.Years)
        {
            Assert.Equal([900001L], year.Lines.Single(l => l.Name == "Kretsbannerkonkurranse").ProjectIds);
            Assert.Empty(year.Lines.Single(l => l.Name == "Leirer").ProjectIds);
        }
    }
}
