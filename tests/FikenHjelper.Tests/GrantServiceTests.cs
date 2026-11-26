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
}
