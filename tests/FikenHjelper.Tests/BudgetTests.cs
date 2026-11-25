using ClosedXML.Excel;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using Microsoft.Extensions.Time.Testing;

namespace FikenHjelper.Tests;

public sealed class BudgetReportBuilderTests
{
    private static readonly FikenProject Leir = new(1, "1", "Landsleir", null, null, null, false);
    private static readonly FikenProject Kurs = new(2, "2", "Kurs for ledere", null, null, null, false);
    private static readonly FikenProject Glemt = new(3, "3", "Nytt prosjekt", null, null, null, false);

    private static FikenJournalEntry Entry(long id, params FikenJournalEntryLine[] lines) =>
        new(id, id, "Fri", (int)id, $"Bilag {id}", "2026-03-01", lines);

    private static FikenJournalEntryLine Line(string account, long amountOre, params long[] projects) =>
        new(amountOre, account, null, projects);

    [Fact]
    public void Build_SumsResultAccountsPerProjectWithResultSign()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                new BudgetLine { Name = "Drift", Amount = -10_000m, ProjectIds = [Leir.ProjectId] },
                new BudgetLine { Name = "Kurs", Amount = 5_000m, ProjectIds = [Kurs.ProjectId] }
            ]
        };

        var entries = new[]
        {
            // Kostnad 1 500 kr på leir: debet 6800, kredit bank.
            Entry(1, Line("6800", 150_000, Leir.ProjectId), Line("1920:10001", -150_000)),
            // Inntekt 8 000 kr på kurs: kredit 3200, debet kunde (underkonto).
            Entry(2, Line("3200", -800_000, Kurs.ProjectId), Line("1500:20001", 800_000)),
        };

        var report = BudgetReportBuilder.Build(budget, [Leir, Kurs], entries);

        Assert.Equal(-1_500m, report.Lines[0].Actual);
        Assert.Equal(8_500m, report.Lines[0].Variance);
        Assert.Equal(8_000m, report.Lines[1].Actual);
        Assert.Equal(0.6m, report.Lines[1].VariancePercent);
        Assert.Equal(6_500m, report.FikenResult);
        Assert.Empty(report.UnallocatedPostings);
        Assert.Empty(report.UnlinkedProjects);
    }

    [Fact]
    public void Build_FlagsUnallocatedPostingsAndUnlinkedProjects_AndIgnoresYearEndAccounts()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines = [new BudgetLine { Name = "Drift", Amount = 0, ProjectIds = [Leir.ProjectId] }]
        };

        var entries = new[]
        {
            Entry(1, Line("6300", 20_000), Line("1920", -20_000)),
            Entry(2, Line("6800", 5_000, Glemt.ProjectId), Line("1920", -5_000)),
            Entry(3, Line("8800", 99_900), Line("2050", -99_900)),
        };

        var report = BudgetReportBuilder.Build(budget, [Leir, Glemt], entries);

        var posting = Assert.Single(report.UnallocatedPostings);
        Assert.Equal("6300", posting.Account);
        Assert.Equal(-200m, posting.Amount);

        var unlinked = Assert.Single(report.UnlinkedProjects);
        Assert.Equal("Nytt prosjekt", unlinked.Name);
        Assert.Equal(-50m, unlinked.Amount);

        Assert.Equal(-250m, report.FikenResult);
        Assert.Equal(report.FikenResult, report.TotalActual + report.UnallocatedTotal + report.UnlinkedTotal);
    }

    [Fact]
    public void Build_UsesManualActualAndFlagsProjectLinkedTwice()
    {
        var budget = new BudgetYear
        {
            Year = 2024,
            Lines =
            [
                new BudgetLine { Name = "A", Amount = 100m, ManualActual = 80m, ProjectIds = [Leir.ProjectId] },
                new BudgetLine { Name = "B", Amount = 0m, ProjectIds = [Leir.ProjectId] }
            ]
        };

        var report = BudgetReportBuilder.Build(budget, [Leir], journalEntries: null);

        Assert.True(report.Lines[0].IsManualActual);
        Assert.Equal(80m, report.Lines[0].Actual);
        Assert.False(report.HasFikenData);
        Assert.Null(report.FikenResult);
        Assert.Empty(report.ProjectsLinkedToSeveralLines);
    }

    [Fact]
    public void Build_FlagsProjectLinkedToSeveralFikenLines()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                new BudgetLine { Name = "A", ProjectIds = [Leir.ProjectId] },
                new BudgetLine { Name = "B", ProjectIds = [Leir.ProjectId] }
            ]
        };

        var report = BudgetReportBuilder.Build(budget, [Leir], journalEntries: []);

        Assert.Equal(Leir.ProjectId, Assert.Single(report.ProjectsLinkedToSeveralLines).ProjectId);
    }

    [Fact]
    public void Build_ProjectOnManualLineIsReportedAsUnlinked()
    {
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines =
            [
                new BudgetLine { Name = "Manuell", ManualActual = 10m, ProjectIds = [Leir.ProjectId] },
                new BudgetLine { Name = "Fra Fiken" }
            ]
        };
        FikenJournalEntry[] entries = [Entry(1, Line("3200", -10_000, Leir.ProjectId), Line("1920:10001", 10_000))];

        var report = BudgetReportBuilder.Build(budget, [Leir], entries);

        Assert.Equal(Leir.ProjectId, Assert.Single(report.UnlinkedProjects).ProjectId);
        Assert.Equal(100m, report.UnlinkedTotal);
    }

    [Theory]
    [InlineData("3200", -800_000L, true, 3200, 8_000)]
    [InlineData("6800:123", 150_000L, true, 6800, -1_500)]
    [InlineData("1920", 150_000L, false, 1920, 0)]
    [InlineData("8800", 99_900L, false, 8800, 0)]
    [InlineData(null, 100L, false, 0, 0)]
    public void TryGetResultAmount_AppliesSignAndAccountRange(string? account, long amountOre, bool expected, int expectedAccount, int expectedAmount)
    {
        var ok = BudgetReportBuilder.TryGetResultAmount(new FikenJournalEntryLine(amountOre, account, null, null), out var parsed, out var amount);

        Assert.Equal(expected, ok);
        Assert.Equal(expectedAccount, parsed);
        Assert.Equal((decimal)expectedAmount, amount);
    }

    [Fact]
    public void PrimaryProjectId_UsesFirstProjectOrNull()
    {
        Assert.Equal(7L, BudgetReportBuilder.PrimaryProjectId(new FikenJournalEntryLine(1, "3000", null, [7, 8])));
        Assert.Null(BudgetReportBuilder.PrimaryProjectId(new FikenJournalEntryLine(1, "3000", null, [])));
        Assert.Null(BudgetReportBuilder.PrimaryProjectId(new FikenJournalEntryLine(1, "3000", null, null)));
    }

    [Fact]
    public void Comparison_AlignsLinesByNameAcrossYears()
    {
        var y2026 = BudgetReportBuilder.Build(
            new BudgetYear { Year = 2026, Lines = [new() { Name = "Drift", Amount = 1 }, new() { Name = "Kryss og tvers", Amount = 2 }] },
            [], null);
        var y2025 = BudgetReportBuilder.Build(
            new BudgetYear { Year = 2025, Lines = [new() { Name = "drift", Amount = 3 }] },
            [], null);

        var comparison = BudgetComparison.Build([y2025, y2026]);

        Assert.Equal([2026, 2025], comparison.Years);
        Assert.Equal(2, comparison.Rows.Count);
        Assert.Equal(3, comparison.Rows[0].ByYear[2025]!.Budget);
        Assert.Null(comparison.Rows[1].ByYear[2025]);
    }
}

public sealed class BudgetWorkbookTests
{
    private static MemoryStream CreateWorkbook()
    {
        using var wb = new XLWorkbook();
        var overview = wb.AddWorksheet("Ark 1");
        overview.Cell("B1").Value = "Budsjett 2026";
        overview.Cell("C1").Value = "Regnskap 2026";
        overview.Cell("D1").Value = "Avvik 2026";
        overview.Cell("E1").Value = "Budsjett 2024";
        overview.Cell("F1").Value = "Regnskap 2024";

        overview.Cell("A2").Value = "Medlemskontingent";
        overview.Cell("B2").Value = 92070;
        overview.Cell("C2").Value = 0;
        overview.Cell("E2").Value = 89000;
        overview.Cell("F2").Value = 85625;

        overview.Cell("A3").Value = "Drift";
        overview.Cell("B3").Value = -13500;
        overview.Cell("E3").Value = -26500;
        overview.Cell("F3").FormulaA1 = "-10188+11813-40-5291";

        overview.Cell("A4").Value = "Kryss og tvers";
        overview.Cell("B4").Value = 100000;

        overview.Cell("A5").Value = "Ekstra tilskudd";
        overview.Cell("F5").Value = 15000;

        overview.Cell("A6").Value = "drift";
        overview.Cell("B6").Value = 1;

        overview.Cell("A7").Value = "Resultat";
        overview.Cell("B7").FormulaA1 = "SUM(B2:B6)";

        var projects = wb.AddWorksheet("Regnskap 2026");
        projects.Cell("A1").Value = "Prosjekter 2026";
        projects.Cell("B1").Value = "Sum";
        projects.Cell("C1").Value = "Budsjettpost";
        projects.Cell("A2").Value = "Drift";
        projects.Cell("C2").Value = "Drift";
        projects.Cell("A3").Value = "landsleir";
        projects.Cell("C3").Value = "drift";
        projects.Cell("A4").Value = "kryss og tvers";
        projects.Cell("C4").Value = "Kryss og tvers";
        projects.Cell("A5").Value = "Ukjent prosjekt";
        projects.Cell("C5").Value = "Drift";

        var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Import_ReadsBudgetLinesAndMapsProjectsByName()
    {
        FikenProject[] fiken =
        [
            new(10, "1", "Drift", null, null, null, false),
            new(11, "2", "Landsleir", null, null, null, false),
            new(12, "3", "Kryss og tvers", null, null, null, false),
        ];

        using var stream = CreateWorkbook();
        var result = BudgetWorkbookImporter.Import(stream, fiken);

        Assert.Equal([2026, 2024], result.Years.Select(y => y.Year));

        var y2026 = result.Years[0];
        Assert.Equal(["Medlemskontingent", "Drift", "Kryss og tvers"], y2026.Lines.Select(l => l.Name));
        Assert.All(y2026.Lines, l => Assert.Null(l.ManualActual));
        Assert.Equal([10L, 11L], y2026.Lines[1].ProjectIds);
        Assert.Equal([12L], y2026.Lines[2].ProjectIds);

        Assert.Contains(result.Warnings, w => w.Contains("2026") && w.Contains("flere ganger"));

        var y2024 = result.Years[1];
        Assert.Equal(["Medlemskontingent", "Drift", "Ekstra tilskudd"], y2024.Lines.Select(l => l.Name));
        Assert.Equal(85625m, y2024.Lines[0].ManualActual);
        Assert.Equal(-26500m, y2024.Lines[1].Amount);
        Assert.Equal(-3706m, y2024.Lines[1].ManualActual);
        Assert.Equal(0m, y2024.Lines[2].Amount);
        Assert.Equal(15000m, y2024.Lines[2].ManualActual);

        Assert.Contains(result.Warnings, w => w.Contains("Ukjent prosjekt"));
        Assert.Contains(result.Warnings, w => w.Contains("Regnskap 2024"));
    }

    [Theory]
    [InlineData("1 500", 1500)]
    [InlineData("-1 234,50", -1234.50)]
    [InlineData("\u22121234,5", -1234.5)]
    [InlineData("1.234,50", 1234.50)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("12,5 kr", 12.5)]
    [InlineData("10.000", 10000)]
    [InlineData("1.234", 1234)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("kr. 1 500", 1500)]
    [InlineData("kr 1 500,-", 1500)]
    [InlineData("1 500,-", 1500)]
    [InlineData("12.5", 12.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("-10.000,25", -10000.25)]
    [InlineData("0.500", 0.5)]
    [InlineData("-0.250", -0.25)]
    [InlineData(".500", 0.5)]
    [InlineData("1234.567", 1234.567)]
    public void ParseNumber_HandlesNorwegianAndMixedFormats(string text, double expected)
    {
        Assert.Equal((decimal)expected, BudgetWorkbookImporter.ParseNumber(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("kr")]
    [InlineData("1,2,3.4.5")]
    [InlineData("12.01.2026")]
    [InlineData("1.2.3")]
    public void ParseNumber_ReturnsNullForNonNumbers(string text)
    {
        Assert.Null(BudgetWorkbookImporter.ParseNumber(text));
    }

    [Fact]
    public void Validate_ReportsMissingAndDuplicateNames()
    {
        var data = new BudgetData
        {
            Years =
            [
                new BudgetYear
                {
                    Year = 2026,
                    Lines = [new BudgetLine { Name = "Drift" }, new BudgetLine { Name = "drift " }, new BudgetLine { Name = " " }]
                }
            ]
        };

        var errors = data.Validate();

        Assert.Contains(errors, e => e.Contains("mangler navn"));
        Assert.Contains(errors, e => e.Contains("Drift"));
    }

    [Fact]
    public void Import_ToleratesGoogleSheetsRelationshipsWithoutIdOrTarget()
    {
        using var original = CreateWorkbook();
        var broken = new MemoryStream();
        original.CopyTo(broken);
        using (var zip = new System.IO.Compression.ZipArchive(broken, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/_rels/workbook.xml.rels")!;
            string xml;
            using (var reader = new StreamReader(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            entry.Delete();
            var patched = zip.CreateEntry("xl/_rels/workbook.xml.rels");
            using var writer = new StreamWriter(patched.Open());
            writer.Write(xml.Replace(
                "</Relationships>",
                "<Relationship Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLinkPath\" TargetMode=\"External\"/></Relationships>"));
        }

        broken.Position = 0;
        var result = BudgetWorkbookImporter.Import(broken, []);

        Assert.Equal(2, result.Years.Count);
    }

    [Fact]
    public void Export_RoundTripsOverviewThroughImporter()
    {
        var report = BudgetReportBuilder.Build(
            new BudgetYear
            {
                Year = 2025,
                Lines =
                [
                    new() { Name = "Medlemskontingent", Amount = 100965m, ManualActual = 92227.5m },
                    new() { Name = "Drift", Amount = -13500m, ManualActual = -102203.39m }
                ]
            },
            [], null);

        var bytes = BudgetWorkbookExporter.Export(BudgetComparison.Build([report]));

        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.True(wb.TryGetWorksheet("Budsjettoversikt", out _));
        Assert.True(wb.TryGetWorksheet("Prosjekter", out _));
        Assert.True(wb.TryGetWorksheet("Kontroll", out _));

        var imported = BudgetWorkbookImporter.Import(new MemoryStream(bytes), []);
        var year = Assert.Single(imported.Years);
        Assert.Equal(92227.5m, year.Lines[0].ManualActual);
        Assert.Equal(-13500m, year.Lines[1].Amount);
    }
}

public sealed class JsonBudgetStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsAndNormalizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fh-budget-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonBudgetStore(dir);

            await store.SaveAsync(new BudgetData
            {
                Years =
                [
                    new BudgetYear { Year = 2025, Lines = [new() { Name = " Drift ", Amount = -1, ProjectIds = [5, 5, 0] }, new() { Name = "  " }] },
                    new BudgetYear { Year = 2026 }
                ]
            }, TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal([2026, 2025], loaded.Years.Select(y => y.Year));
            var line = Assert.Single(loaded.FindYear(2025)!.Lines);
            Assert.Equal("Drift", line.Name);
            Assert.Equal([5L], line.ProjectIds);
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

public sealed class FikenDataCacheTests
{
    [Fact]
    public async Task ConcurrentRequestsShareOneFetch()
    {
        var cache = new FikenDataCache();
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<IReadOnlyList<int>> Fetch()
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return [1, 2];
        }

        var first = cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken);
        var second = cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.Equal([1, 2], await first);
        Assert.Equal([1, 2], await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RefreshFetchesAgainEvenWhenClockHasNotMoved()
    {
        var time = new FakeTimeProvider();
        var cache = new FikenDataCache(time);
        var calls = 0;
        Task<IReadOnlyList<int>> Fetch() => Task.FromResult<IReadOnlyList<int>>([Interlocked.Increment(ref calls)]);

        await cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken);
        Assert.Equal([1], await cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken));

        Assert.Equal([2], await cache.GetOrFetchAsync("k", refresh: true, Fetch, ct: TestContext.Current.CancellationToken));
        Assert.Equal([3], await cache.GetOrFetchAsync("k", refresh: true, Fetch, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiresAfterLifetime()
    {
        var time = new FakeTimeProvider();
        var cache = new FikenDataCache(time);
        var calls = 0;
        Task<IReadOnlyList<int>> Fetch() => Task.FromResult<IReadOnlyList<int>>([Interlocked.Increment(ref calls)]);

        await cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal([1], await cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal([2], await cache.GetOrFetchAsync("k", refresh: false, Fetch, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentRefreshSharesFetchThatStartedAfterRequest()
    {
        var cache = new FikenDataCache(new FakeTimeProvider());
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<IReadOnlyList<int>> Fetch()
        {
            var n = Interlocked.Increment(ref calls);
            await release.Task;
            return [n];
        }

        var first = cache.GetOrFetchAsync("k", refresh: true, Fetch, ct: TestContext.Current.CancellationToken);
        var second = cache.GetOrFetchAsync("k", refresh: true, Fetch, ct: TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.Equal([1], await first);
        Assert.Equal([1], await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClearDuringFetchDoesNotStoreStaleResult()
    {
        var cache = new FikenDataCache(new FakeTimeProvider());
        var calls = 0;
        var release = new TaskCompletionSource();
        async Task<IReadOnlyList<int>> Slow()
        {
            var n = Interlocked.Increment(ref calls);
            await release.Task;
            return [n];
        }

        var pending = cache.GetOrFetchAsync("k", refresh: false, Slow, ct: TestContext.Current.CancellationToken);
        cache.Clear();
        release.SetResult();
        Assert.Equal([1], await pending);

        Assert.Equal([2], await cache.GetOrFetchAsync("k", refresh: false, Slow, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitingForGateHonoursCancellation()
    {
        var cache = new FikenDataCache(new FakeTimeProvider());
        var release = new TaskCompletionSource();
        async Task<IReadOnlyList<int>> Slow()
        {
            await release.Task;
            return [1];
        }

        var pending = cache.GetOrFetchAsync("k", refresh: false, Slow, ct: TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var waiting = cache.GetOrFetchAsync("k", refresh: false, Slow, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        Assert.Equal([1], await pending);
    }
}
