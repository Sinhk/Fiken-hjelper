using System.Net;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Grants;

namespace FikenHjelper.Tests;

public sealed class GrantServiceTests : IDisposable
{
    private readonly FakeFikenApi _api = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fh-grants-" + Guid.NewGuid().ToString("N"));

    public GrantServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private GrantService CreateService(string slug = "test-as", TimeProvider? time = null)
    {
        var configStore = _api.CreateStore(slug);
        var client = _api.CreateClient(configStore);
        var budgetStore = new JsonBudgetStore(_dir);
        var cache = new FikenDataCache();
        var reports = new BudgetReportService(client, budgetStore, configStore, cache);
        return new GrantService(new JsonGrantStore(_dir), reports, client, cache, time ?? new ManualTimeProvider(new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    private static string EntryJson(long id, string account, long ore, long? project, string date) =>
        $$"""{"journalEntryId":{{id}},"journalEntryNumber":{{id}},"description":"Bilag {{id}}","date":"{{date}}","lines":[{"amount":{{ore}},"account":"{{account}}","projectId":[{{(project is { } p ? p.ToString() : "")}}]},{"amount":{{-ore}},"account":"1920"}]}""";

    [Fact]
    public async Task LoadFiken_FetchesPerYearAndBuildsOverview()
    {
        _api.Get("companies/test-as/projects", $$"""[{"projectId":{{GrantTestData.Kurs}},"name":"Lederkurs 2027","completed":false}]""")
            .Get("companies/test-as/accounts", """[{"code":"6800","name":"Kontorrekvisita"},{"code":"3400","name":"Offentlige tilskudd"}]""")
            .Get("companies/test-as/journalEntries", "[" + string.Join(",",
                EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-03-01"),
                EntryJson(2, "3400", -5000000, null, "2027-04-01")) + "]");
        var service = CreateService();
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        grant.GrantedAmount = 50000;
        grant.DecisionDate = new DateOnly(2027, 1, 10);
        grant.Payments.Add(new GrantPayment { JournalEntryId = 2, Date = new DateOnly(2027, 4, 1) });

        var years = service.YearsFor([grant]);
        var data = await service.LoadFikenAsync(years, ct: TestContext.Current.CancellationToken);
        var overview = GrantService.BuildOverview([grant], data);

        Assert.Equal([2027], years);
        Assert.True(data.HasCompany);
        var report = Assert.Single(overview.Reports);
        Assert.Equal(1500m, report.Used);
        Assert.Equal(50000m, report.Received);
        Assert.Equal(0m, report.Remaining);
        var usage = Assert.Single(overview.AccountUsage);
        Assert.Equal(3400, usage.Account);
        Assert.Equal("Offentlige tilskudd", usage.AccountName);
        Assert.All(_api.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task EnsureYears_OnlyFetchesMissingYears()
    {
        _api.Get("companies/test-as/projects", "[]")
            .Get("companies/test-as/accounts", "[]")
            .Get("companies/test-as/journalEntries", "[" + EntryJson(1, "6800", 100000, GrantTestData.Kurs, "2027-03-01") + "]");
        var service = CreateService();

        var data = await service.LoadFikenAsync([2027], ct: TestContext.Current.CancellationToken);
        var before = _api.Calls.Count(c => c.PathAndQuery.Contains("journalEntries"));
        await service.EnsureYearsAsync(data, [2027], ct: TestContext.Current.CancellationToken);
        Assert.Equal(before, _api.Calls.Count(c => c.PathAndQuery.Contains("journalEntries")));

        await service.EnsureYearsAsync(data, [2026, 2027], ct: TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, _api.Calls.Count(c => c.PathAndQuery.Contains("journalEntries")));
        Assert.Equal([2026, 2027], data.Years.Order());
        Assert.Single(data.Entries!);
    }

    private void RouteYears(Func<int, HttpResponseMessage> responder) =>
        _api.Get("companies/test-as/projects", "[]")
            .Get("companies/test-as/accounts", "[]")
            .On(HttpMethod.Get, "companies/test-as/journalEntries", (req, _) =>
            {
                var query = req.RequestUri!.Query;
                var start = query.IndexOf("dateGe=", StringComparison.Ordinal) + 7;
                return responder(int.Parse(query.AsSpan(start, 4)));
            });

    [Fact]
    public async Task EnsureYears_PartialFailure_KeepsFinishedYearsAndRetriesTheFailedOne()
    {
        var failing = true;
        RouteYears(year => year == 2026 && failing
            ? FakeFikenApi.Json("{}", HttpStatusCode.BadRequest)
            : FakeFikenApi.Json("[" + EntryJson(year, "6800", 100000, GrantTestData.Kurs, $"{year}-03-01") + "]"));
        var service = CreateService();
        var data = await service.LoadFikenAsync([], ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(() => service.EnsureYearsAsync(data, [2025, 2026], ct: TestContext.Current.CancellationToken));

        Assert.Equal([2025], data.Years.Order());
        Assert.Equal([2025L], data.Entries!.Select(e => e.JournalEntryId));

        failing = false;
        await service.EnsureYearsAsync(data, [2025, 2026], ct: TestContext.Current.CancellationToken);
        Assert.Equal([2025, 2026], data.Years.Order());
        Assert.Equal([2025L, 2026L], data.Entries!.Select(e => e.JournalEntryId));
    }

    [Fact]
    public async Task EnsureYears_Refresh_ReplacesTheYearsEntries()
    {
        var version = 1;
        RouteYears(_ => FakeFikenApi.Json("[" + EntryJson(version, "6800", 100000, GrantTestData.Kurs, "2027-03-01") + "]"));
        var service = CreateService();
        var data = await service.LoadFikenAsync([2027], ct: TestContext.Current.CancellationToken);
        Assert.Equal([1L], data.Entries!.Select(e => e.JournalEntryId));

        version = 2;
        await service.EnsureYearsAsync(data, [2027], refresh: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal([2L], data.Entries!.Select(e => e.JournalEntryId));
    }

    [Fact]
    public async Task EnsureYears_ConcurrentCalls_FetchEachYearOnce()
    {
        RouteYears(year => FakeFikenApi.Json("[" + EntryJson(year, "6800", 100000, GrantTestData.Kurs, $"{year}-03-01") + "]"));
        var service = CreateService();
        var data = await service.LoadFikenAsync([], ct: TestContext.Current.CancellationToken);

        await Task.WhenAll(
            service.EnsureYearsAsync(data, [2026, 2027], ct: TestContext.Current.CancellationToken),
            service.EnsureYearsAsync(data, [2027, 2026], ct: TestContext.Current.CancellationToken),
            service.EnsureYearsAsync(data, [2027], ct: TestContext.Current.CancellationToken));

        Assert.Equal(2, _api.Calls.Count(c => c.PathAndQuery.Contains("journalEntries")));
        Assert.Equal([2026L, 2027L], data.Entries!.Select(e => e.JournalEntryId).Order());
    }

    private static Grant Named(string name) => new() { Name = name, Grantor = "Eksempel kommune", Kind = GrantKind.Operating };

    [Fact]
    public async Task SaveGrant_NewGrant_IsAddedAndAnExistingIdIsRefused()
    {
        var service = CreateService();
        var grant = Named("Drift");

        Assert.Empty(await service.SaveGrantAsync(grant, null, TestContext.Current.CancellationToken));
        Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants);

        var again = GrantCloner.Clone(grant);
        Assert.NotEmpty(await service.SaveGrantAsync(again, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveGrant_ChangedInTheMeantime_GivesConflictAndKeepsTheStoredVersion()
    {
        var service = CreateService();
        var grant = Named("Drift");
        await service.SaveGrantAsync(grant, null, TestContext.Current.CancellationToken);
        var opened = GrantCloner.Clone((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants[0]);

        // En annen økt endrer det lagrede tilskuddet.
        var other = GrantCloner.Clone(opened);
        other.Grantor = "Annen giver";
        Assert.Empty(await service.SaveGrantAsync(other, opened, TestContext.Current.CancellationToken));

        var mine = GrantCloner.Clone(opened);
        mine.Name = "Mitt navn";
        var errors = await service.SaveGrantAsync(mine, opened, TestContext.Current.CancellationToken);

        Assert.Contains(errors, e => e.Contains("endret"));
        var stored = Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants);
        Assert.Equal("Drift", stored.Name);
        Assert.Equal("Annen giver", stored.Grantor);
    }

    [Fact]
    public async Task SaveGrant_DeletedInTheMeantime_IsNotResurrected()
    {
        var service = CreateService();
        var grant = Named("Drift");
        await service.SaveGrantAsync(grant, null, TestContext.Current.CancellationToken);
        var opened = GrantCloner.Clone((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants[0]);
        Assert.Empty(await service.DeleteGrantAsync(grant.Id, TestContext.Current.CancellationToken));

        var errors = await service.SaveGrantAsync(GrantCloner.Clone(opened), opened, TestContext.Current.CancellationToken);

        Assert.Contains(errors, e => e.Contains("slettet"));
        Assert.Empty((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants);
        Assert.Empty(await service.DeleteGrantAsync(grant.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveGrant_ConcurrentSavesOfDifferentGrants_AreAllKept()
    {
        var service = CreateService();
        var grants = Enumerable.Range(1, 12).Select(i => Named($"Tilskudd {i}")).ToList();

        var results = await Task.WhenAll(grants.Select(g => service.SaveGrantAsync(g, null, TestContext.Current.CancellationToken)));

        Assert.All(results, Assert.Empty);
        Assert.Equal(12, (await service.LoadAsync(TestContext.Current.CancellationToken)).Grants.Count);
    }

    [Fact]
    public async Task SaveGrant_InvalidLink_IsRefusedAndNothingIsWritten()
    {
        var service = CreateService();
        var grant = Named("Drift");
        grant.Link = "javascript:alert(1)";

        var errors = await service.SaveGrantAsync(grant, null, TestContext.Current.CancellationToken);

        Assert.Contains(errors, e => e.Contains("http"));
        Assert.Empty((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants);

        grant.Link = "https://example.no/soknad";
        Assert.Empty(await service.SaveGrantAsync(grant, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadFiken_WithoutCompany_ReturnsNoEntriesAndNoCalls()
    {
        var service = CreateService(slug: "");
        var data = await service.LoadFikenAsync([2027], ct: TestContext.Current.CancellationToken);
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);

        var report = Assert.Single(GrantService.BuildOverview([grant], data).Reports);

        Assert.False(data.HasCompany);
        Assert.Null(data.Entries);
        Assert.True(report.UsedNeedsFikenData);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Save_RejectsInvalidDataAndKeepsFile()
    {
        var service = CreateService();
        var data = new GrantData { Grants = [new Grant { Name = "" }] };

        var errors = await service.SaveAsync(data, TestContext.Current.CancellationToken);

        Assert.NotEmpty(errors);
        Assert.False(File.Exists(Path.Combine(_dir, "grants.json")));

        data.Grants[0].Name = "Driftstilskudd 2027";
        Assert.Empty(await service.SaveAsync(data, TestContext.Current.CancellationToken));
        Assert.Single((await service.LoadAsync(TestContext.Current.CancellationToken)).Grants);
    }
}

public sealed class GrantOverviewLogicTests
{
    private static readonly DateOnly Today = new(2027, 6, 1);

    private static Grant G(GrantStatus status, DateOnly? application = null, DateOnly? report = null) => new()
    {
        Name = "Test",
        Status = status,
        ApplicationDeadline = application,
        ReportDeadline = report
    };

    [Fact]
    public void Deadlines_MarksUpcomingWithin30DaysAndOverdue()
    {
        var upcoming = GrantOverviewLogic.Deadlines(G(GrantStatus.Planned, application: Today.AddDays(30)), Today);
        var tooFar = GrantOverviewLogic.Deadlines(G(GrantStatus.Planned, application: Today.AddDays(31)), Today);
        var overdue = GrantOverviewLogic.Deadlines(G(GrantStatus.Granted, report: Today.AddDays(-1)), Today);
        var dueToday = GrantOverviewLogic.Deadlines(G(GrantStatus.Granted, report: Today), Today);

        Assert.Equal(GrantDeadlineState.Upcoming, Assert.Single(upcoming).State);
        Assert.Empty(tooFar);
        var o = Assert.Single(overdue);
        Assert.Equal(GrantDeadlineState.Overdue, o.State);
        Assert.Equal(GrantDeadlineKind.Report, o.Kind);
        Assert.Equal(0, Assert.Single(dueToday).Days);
    }

    [Theory]
    [InlineData(GrantStatus.Rejected)]
    [InlineData(GrantStatus.Withdrawn)]
    [InlineData(GrantStatus.Closed)]
    public void Deadlines_NoneForFinishedGrants(GrantStatus status) =>
        Assert.Empty(GrantOverviewLogic.Deadlines(G(status, Today.AddDays(-5), Today.AddDays(-5)), Today));

    [Fact]
    public void Deadlines_ApplicationDeadlineOnlyBeforeSubmitted_ReportNotAfterReported()
    {
        Assert.Empty(GrantOverviewLogic.Deadlines(G(GrantStatus.Submitted, application: Today.AddDays(-3)), Today));
        Assert.Empty(GrantOverviewLogic.Deadlines(G(GrantStatus.Reported, report: Today.AddDays(-3)), Today));
        Assert.Single(GrantOverviewLogic.Deadlines(G(GrantStatus.Submitted, report: Today.AddDays(5)), Today));
    }

    [Fact]
    public void OverlapsYear_UsesUsePeriodOrDecisionDate()
    {
        var period = new Grant { Name = "A", UseFrom = new DateOnly(2026, 9, 1), UseTo = new DateOnly(2027, 2, 28) };
        var decided = new Grant { Name = "B", DecisionDate = new DateOnly(2027, 5, 1), UseFrom = new DateOnly(2028, 1, 1), UseTo = new DateOnly(2028, 12, 31) };
        var openEnded = new Grant { Name = "C", UseFrom = new DateOnly(2026, 1, 1) };
        var noDates = new Grant { Name = "D" };

        Assert.True(GrantOverviewLogic.OverlapsYear(period, 2026));
        Assert.True(GrantOverviewLogic.OverlapsYear(period, 2027));
        Assert.False(GrantOverviewLogic.OverlapsYear(period, 2028));
        Assert.True(GrantOverviewLogic.OverlapsYear(decided, 2027));
        Assert.True(GrantOverviewLogic.OverlapsYear(decided, 2028));
        Assert.False(GrantOverviewLogic.OverlapsYear(decided, 2026));
        Assert.True(GrantOverviewLogic.OverlapsYear(openEnded, 2030));
        Assert.False(GrantOverviewLogic.OverlapsYear(openEnded, 2025));
        Assert.True(GrantOverviewLogic.OverlapsYear(noDates, 2020));
    }

    [Fact]
    public void Filter_ByYearAndStatus_OrdersByGrantor()
    {
        var a = new Grant { Name = "A", Grantor = "Zeta", Status = GrantStatus.Granted, UseFrom = new DateOnly(2027, 1, 1), UseTo = new DateOnly(2027, 12, 31) };
        var b = new Grant { Name = "B", Grantor = "Alfa", Status = GrantStatus.Submitted, UseFrom = new DateOnly(2027, 1, 1), UseTo = new DateOnly(2027, 12, 31) };
        var c = new Grant { Name = "C", Grantor = "Alfa", Status = GrantStatus.Granted, UseFrom = new DateOnly(2025, 1, 1), UseTo = new DateOnly(2025, 12, 31) };
        var reports = GrantReportBuilder.BuildAll([a, b, c], null);

        Assert.Equal(["B", "A"], GrantOverviewLogic.Filter(reports, 2027, null).Select(r => r.Grant.Name));
        Assert.Equal(["A"], GrantOverviewLogic.Filter(reports, 2027, GrantStatus.Granted).Select(r => r.Grant.Name));
        Assert.Equal(3, GrantOverviewLogic.Filter(reports, null, null).Count);
    }

    [Fact]
    public void Clone_IsDeepCopy()
    {
        var grant = new Grant { Name = "A", ProjectIds = [1], ProjectShares = new() { [1] = 50m }, Payments = [new GrantPayment { Amount = 5m }] };
        var copy = GrantCloner.Clone(grant);
        copy.ProjectIds.Add(2);
        copy.ProjectShares![1] = 10m;
        copy.Payments[0].Amount = 9m;

        Assert.Equal([1L], grant.ProjectIds);
        Assert.Equal(50m, grant.ProjectShares[1]);
        Assert.Equal(5m, grant.Payments[0].Amount);
    }

    private static GrantReport Report(Grant grant, bool withFiken = true) =>
        GrantReportBuilder.Build(grant, [grant], withFiken ? [] : null);

    [Fact]
    public void Totals_AreDashWhenNoRowHasValue_AndPartialWhenSomeRowsLackIt()
    {
        var a = new Grant { Name = "A", Kind = GrantKind.Operating, GrantedAmount = 10000, AppliedAmount = 12000, ManualUses = [new GrantManualUse { Amount = 4000 }] };
        var b = new Grant { Name = "B", Kind = GrantKind.Operating, GrantedAmount = 5000 };
        var c = new Grant { Name = "C", Kind = GrantKind.Project, ProjectIds = [GrantTestData.Kurs] };
        var rows = new[] { Report(a), Report(b), Report(c, withFiken: false) };

        var totals = GrantOverviewLogic.Totals(rows);

        Assert.Equal(new GrantTotal(15000m, 2, 3), totals.Granted);
        Assert.True(totals.Granted.IsPartial);
        Assert.Equal(new GrantTotal(12000m, 1, 3), totals.Applied);
        // Brukt: A har manuell bruk, B er ikke registrert, C mangler Fiken-data.
        Assert.Equal(new GrantTotal(4000m, 1, 3), totals.Used);
        Assert.False(GrantOverviewLogic.Totals([Report(b)]).Used.HasValue);
        Assert.False(GrantOverviewLogic.Totals([]).Granted.HasValue);
        Assert.False(GrantOverviewLogic.Totals([Report(a)]).Used.IsPartial);
    }

    [Fact]
    public void Totals_ReceivedIsUnknownForLinkedPaymentsWithoutFikenData()
    {
        var grant = new Grant { Name = "A", Kind = GrantKind.Operating, GrantedAmount = 10000, Payments = [new GrantPayment { JournalEntryId = 5 }, new GrantPayment { Amount = 100m }] };
        var withoutFiken = Report(grant, withFiken: false);
        var withFiken = Report(grant);

        Assert.Null(GrantOverviewLogic.ReceivedOrNull(withoutFiken));
        Assert.Null(GrantOverviewLogic.RemainingOrNull(withoutFiken));
        // Med Fiken-data men uten posteringen er koblingen ukjent (Missing).
        Assert.Null(GrantOverviewLogic.ReceivedOrNull(withFiken));
        Assert.Equal(0, GrantOverviewLogic.Totals([withoutFiken]).Received.Counted);
    }

    [Fact]
    public void SelectableYears_IncludesReportDeadlineAndEveryYearInThePeriod_WithACap()
    {
        var grant = new Grant
        {
            Name = "A",
            UseFrom = new DateOnly(2024, 6, 1),
            UseTo = new DateOnly(2027, 5, 31),
            ReportDeadline = new DateOnly(2028, 3, 1)
        };
        var typo = new Grant { Name = "B", UseFrom = new DateOnly(2000, 1, 1), UseTo = new DateOnly(9999, 1, 1) };

        Assert.Equal([2028, 2027, 2026, 2025, 2024], GrantOverviewLogic.SelectableYears([grant], 2026).Where(y => y >= 2024));
        var years = GrantOverviewLogic.SelectableYears([typo], 2026);
        Assert.True(years.Count <= GrantOverviewLogic.MaxPeriodYears + 4);
    }

    [Fact]
    public void BuildOverview_ReportsEachShareIssueOnlyOnce()
    {
        var a = GrantTestData.Project("A", GrantTestData.Kurs);
        var b = GrantTestData.Project("B", GrantTestData.Kurs);

        var overview = GrantService.BuildOverview([a, b], GrantFikenData.None);

        Assert.Single(overview.Issues, i => i.Kind == GrantIssueKind.ShareMissing);
        Assert.Equal(2, overview.Reports.Count);
    }

    [Fact]
    public void Clone_DeepCopiesAccountRangesAndManualUses()
    {
        var grant = new Grant
        {
            Name = "A",
            Kind = GrantKind.Operating,
            AccountRanges = [new AccountRange { From = 6800, To = 6899 }],
            ManualUses = [new GrantManualUse { Amount = 10m, Description = "x" }]
        };
        var copy = GrantCloner.Clone(grant);
        copy.AccountRanges[0].To = 6999;
        copy.ManualUses[0].Amount = 99m;
        copy.ManualUses[0].Description = "y";

        Assert.Equal(6899, grant.AccountRanges[0].To);
        Assert.Equal(10m, grant.ManualUses[0].Amount);
        Assert.Equal("x", grant.ManualUses[0].Description);
        Assert.False(GrantCloner.AreEquivalent(grant, copy));
        Assert.True(GrantCloner.AreEquivalent(grant, GrantCloner.Clone(grant)));
    }
}
