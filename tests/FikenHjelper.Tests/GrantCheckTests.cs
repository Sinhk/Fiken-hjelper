using System.Text.Json;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Grants;
using FikenHjelper.Core.QualityCheck;
using FikenHjelper.Mcp.Services;
using FikenHjelper.Mcp.Tools;
using ModelContextProtocol;

namespace FikenHjelper.Tests;

/// <summary>Felles oppsett: falsk Fiken-API, midlertidig datamappe og en tjeneste som henter posteringer.</summary>
public abstract class GrantFikenTestBase : IDisposable
{
    protected static readonly DateOnly Ref = new(2027, 6, 1);

    internal FakeFikenApi Api { get; } = new();
    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "fh-grantcheck-" + Guid.NewGuid().ToString("N"));

    protected GrantFikenTestBase() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        Api.Dispose();
        if (Directory.Exists(Dir))
        {
            Directory.Delete(Dir, recursive: true);
        }
    }

    protected static string EntryJson(long id, string account, long ore, long? project, string date) =>
        $$"""{"journalEntryId":{{id}},"journalEntryNumber":{{id}},"description":"Bilag {{id}}","date":"{{date}}","lines":[{"amount":{{ore}},"account":"{{account}}","projectId":[{{(project is { } p ? p.ToString() : "")}}]},{"amount":{{-ore}},"account":"1920"}]}""";

    protected GrantService CreateService(string slug = "test-as")
    {
        var configStore = Api.CreateStore(slug);
        var client = Api.CreateClient(configStore);
        var cache = new FikenDataCache();
        var reports = new BudgetReportService(client, new JsonBudgetStore(Dir), configStore, cache);
        return new GrantService(new JsonGrantStore(Dir), reports, client, cache, new ManualTimeProvider(new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    protected async Task<GrantFikenData> LoadAsync(IReadOnlyList<Grant> grants, params string[] entries)
    {
        Api.Get("companies/test-as/projects", $$"""[{"projectId":{{GrantTestData.Kurs}},"name":"Lederkurs 2027","completed":false},{"projectId":{{GrantTestData.Leir}},"name":"Leir 2027","completed":false}]""")
            .Get("companies/test-as/accounts", """[{"code":"6800","name":"Kontorrekvisita"},{"code":"3400","name":"Offentlige tilskudd"}]""")
            .Get("companies/test-as/journalEntries", "[" + string.Join(",", entries) + "]");
        var service = CreateService();
        return await service.LoadFikenAsync(service.YearsFor(grants), ct: TestContext.Current.CancellationToken);
    }
}

public sealed class GrantCheckTests : GrantFikenTestBase
{
    private static Grant Make(string name, GrantStatus status = GrantStatus.Granted, params long[] projects)
    {
        var grant = GrantTestData.Project(name, projects);
        grant.Status = status;
        return grant;
    }

    private static GrantCheckSection Build(IReadOnlyList<Grant> grants, GrantFikenData? data = null, DateOnly? date = null) =>
        GrantCheckRules.Build(grants, data ?? GrantFikenData.None, date ?? Ref);

    [Theory]
    [InlineData(30, true, QualityStatus.Open)]
    [InlineData(0, true, QualityStatus.Open)]
    [InlineData(31, false, QualityStatus.Ok)]
    [InlineData(-1, true, QualityStatus.Overdue)]
    public void ReportDeadline_BoundariesAt30Days(int daysAhead, bool expectFinding, QualityStatus expected)
    {
        var grant = Make("Kurs", GrantStatus.Submitted);
        grant.ReportDeadline = Ref.AddDays(daysAhead);

        var section = Build([grant]);

        if (!expectFinding)
        {
            Assert.DoesNotContain(section.Findings, f => f.Kind == GrantCheckKind.ReportDeadline);
            return;
        }

        var finding = Assert.Single(section.Findings, f => f.Kind == GrantCheckKind.ReportDeadline);
        Assert.Equal(expected, finding.Status);
        Assert.Equal($"tilskudd/{grant.Id}", finding.Href);
        Assert.Contains(Ref.AddDays(daysAhead).ToString("dd.MM.yyyy"), finding.Detail);
    }

    [Theory]
    [InlineData(GrantStatus.Rejected)]
    [InlineData(GrantStatus.Withdrawn)]
    [InlineData(GrantStatus.Reported)]
    [InlineData(GrantStatus.Closed)]
    public void ReportDeadline_NotForFinishedStatuses(GrantStatus status)
    {
        var grant = Make("Ferdig", status);
        grant.ReportDeadline = Ref.AddDays(-10);

        Assert.DoesNotContain(Build([grant]).Findings, f => f.Kind == GrantCheckKind.ReportDeadline);
    }

    [Fact]
    public void ApplicationDeadline_OnlyForPlanned()
    {
        var planned = Make("Planlagt", GrantStatus.Planned);
        planned.ApplicationDeadline = Ref.AddDays(30);
        var passed = Make("Passert", GrantStatus.Planned);
        passed.ApplicationDeadline = Ref.AddDays(-2);
        var sent = Make("Sendt", GrantStatus.Submitted);
        sent.ApplicationDeadline = Ref.AddDays(-2);
        var far = Make("Langt fram", GrantStatus.Planned);
        far.ApplicationDeadline = Ref.AddDays(31);

        var findings = Build([planned, passed, sent, far]).Findings;

        Assert.Equal(2, findings.Count);
        Assert.Equal(QualityStatus.Open, findings.Single(f => f.GrantId == planned.Id).Status);
        Assert.Equal(QualityStatus.Overdue, findings.Single(f => f.GrantId == passed.Id).Status);
        Assert.All(findings, f => Assert.Equal(GrantCheckKind.ApplicationDeadline, f.Kind));
    }

    [Fact]
    public async Task UsedExceedsGranted_ReportsOveruse()
    {
        var grant = Make("Overforbruk", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 1000;
        var data = await LoadAsync([grant], EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-03-01"));

        var finding = Assert.Single(Build([grant], data).Findings, f => f.Kind == GrantCheckKind.UsedExceedsGranted);

        Assert.Equal(500m, finding.Amount);
        Assert.Equal(QualityStatus.CheckManually, finding.Status);
    }

    [Fact]
    public async Task UsedExceedsGranted_NotWhenEqual()
    {
        var grant = Make("Akkurat", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 1500;
        var data = await LoadAsync([grant], EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-03-01"));

        Assert.DoesNotContain(Build([grant], data).Findings, f => f.Kind == GrantCheckKind.UsedExceedsGranted);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, true)]
    [InlineData(59, false)]
    public void NotReceived_BoundaryAt60Days(int daysSinceDecision, bool expectFinding)
    {
        var grant = Make("Venter", GrantStatus.Granted);
        grant.GrantedAmount = 50000;
        grant.DecisionDate = Ref.AddDays(-daysSinceDecision);

        var findings = Build([grant]).Findings.Where(f => f.Kind == GrantCheckKind.NotReceived).ToList();

        Assert.Equal(expectFinding, findings.Count == 1);
        if (expectFinding)
        {
            Assert.Equal(50000m, findings[0].Amount);
            Assert.Equal(QualityStatus.Overdue, findings[0].Status);
        }
    }

    [Fact]
    public void NotReceived_PartiallyGrantedAndPartlyPaid_ReportsRemaining()
    {
        var grant = Make("Delvis", GrantStatus.PartiallyGranted);
        grant.GrantedAmount = 40000;
        grant.DecisionDate = Ref.AddDays(-100);
        grant.Payments.Add(new GrantPayment { Amount = 10000, Date = Ref.AddDays(-50) });

        var finding = Assert.Single(Build([grant]).Findings, f => f.Kind == GrantCheckKind.NotReceived);

        Assert.Equal(30000m, finding.Amount);
    }

    [Fact]
    public void NotReceived_NotWhenFullyReceived_OrOtherStatus_OrNoDecisionOrAmount()
    {
        var paid = Make("Betalt", GrantStatus.Granted);
        paid.GrantedAmount = 10000;
        paid.DecisionDate = Ref.AddDays(-90);
        paid.Payments.Add(new GrantPayment { Amount = 10000, Date = Ref.AddDays(-30) });
        var sent = Make("Sendt", GrantStatus.Submitted);
        sent.GrantedAmount = 10000;
        sent.DecisionDate = Ref.AddDays(-90);
        var noDecision = Make("Uten vedtaksdato", GrantStatus.Granted);
        noDecision.GrantedAmount = 10000;
        var noAmount = Make("Uten beløp", GrantStatus.Granted);
        noAmount.DecisionDate = Ref.AddDays(-90);

        Assert.DoesNotContain(Build([paid, sent, noDecision, noAmount]).Findings, f => f.Kind == GrantCheckKind.NotReceived);
    }

    [Fact]
    public void NotReceived_LinkedPaymentWithoutFikenData_IsNotAFalseFinding_ButNoted()
    {
        var grant = Make("Koblet", GrantStatus.Granted);
        grant.GrantedAmount = 10000;
        grant.DecisionDate = Ref.AddDays(-90);
        grant.Payments.Add(new GrantPayment { JournalEntryId = 77, Date = Ref.AddDays(-30) });

        var section = Build([grant]);

        Assert.Empty(section.Findings);
        Assert.NotNull(section.MissingDataNote);
        Assert.Contains("Mangler Fiken-data", section.MissingDataNote);
        Assert.Equal(QualityStatus.CheckManually, section.Status);
    }

    [Fact]
    public async Task LinkedPaymentMissingInFiken_ReportsPaymentNotFound_NotNotReceived()
    {
        var grant = Make("Mangler bilag", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 10000;
        grant.DecisionDate = Ref.AddDays(-90);
        grant.Payments.Add(new GrantPayment { JournalEntryId = 999, Date = new DateOnly(2027, 3, 1) });
        var data = await LoadAsync([grant], EntryJson(1, "6800", 10000, GrantTestData.Kurs, "2027-03-01"));

        var findings = Build([grant], data).Findings;

        Assert.Contains(findings, f => f.Kind == GrantCheckKind.PaymentNotFound);
        Assert.DoesNotContain(findings, f => f.Kind == GrantCheckKind.NotReceived);
    }

    [Fact]
    public async Task CostsOutsidePeriod_SumsAmountAndVouchers_IgnoresBoundariesAndOthers()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.UseFrom = new DateOnly(2027, 2, 1);
        grant.UseTo = new DateOnly(2027, 4, 30);
        var data = await LoadAsync([grant],
            EntryJson(1, "6800", 100000, GrantTestData.Kurs, "2027-01-15"),
            EntryJson(2, "6800", 50000, GrantTestData.Kurs, "2027-05-01"),
            EntryJson(3, "6800", 70000, GrantTestData.Kurs, "2027-02-01"),
            EntryJson(4, "6800", 70000, GrantTestData.Kurs, "2027-04-30"),
            EntryJson(5, "6800", 90000, GrantTestData.Leir, "2027-01-20"),
            EntryJson(6, "3400", -90000, GrantTestData.Kurs, "2027-01-20"),
            EntryJson(7, "6800", 20000, GrantTestData.Kurs, "2027-06-02"));

        var finding = Assert.Single(Build([grant], data).Findings, f => f.Kind == GrantCheckKind.CostsOutsidePeriod);

        Assert.Equal(1500m, finding.Amount);
        Assert.Contains("2 bilag", finding.Detail);
        Assert.Contains("01.02.2027–30.04.2027", finding.Detail);
    }

    [Fact]
    public async Task CostsOutsidePeriod_NotWithoutPeriod_OrForOperatingGrants()
    {
        var noPeriod = Make("Uten periode", GrantStatus.Granted, GrantTestData.Kurs);
        var operating = new Grant
        {
            Name = "Drift",
            Kind = GrantKind.Operating,
            Status = GrantStatus.Granted,
            UseFrom = new DateOnly(2027, 2, 1),
            AccountRanges = [new AccountRange { From = 6800, To = 6899 }]
        };
        var data = await LoadAsync([noPeriod, operating], EntryJson(1, "6800", 100000, GrantTestData.Kurs, "2027-01-15"));

        Assert.DoesNotContain(Build([noPeriod, operating], data).Findings, f => f.Kind == GrantCheckKind.CostsOutsidePeriod);
    }

    [Fact]
    public void CostsOutsidePeriod_WithoutFikenData_GivesNoteInsteadOfFinding()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.UseFrom = new DateOnly(2027, 2, 1);

        var section = Build([grant]);

        Assert.Empty(section.Findings);
        Assert.Contains("Kostnader utenfor bruksperioden", section.MissingDataNote);
    }

    [Fact]
    public void InvalidShares_ReportedOnEveryGrantOnTheProject()
    {
        var a = Make("A", GrantStatus.Granted, GrantTestData.Kurs);
        a.ProjectShares = new() { [GrantTestData.Kurs] = 70m };
        var b = Make("B", GrantStatus.Granted, GrantTestData.Kurs);
        b.ProjectShares = new() { [GrantTestData.Kurs] = 50m };
        var rejected = Make("C", GrantStatus.Rejected, GrantTestData.Kurs);

        var findings = Build([a, b, rejected]).Findings.Where(f => f.Kind == GrantCheckKind.InvalidShares).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), findings.Select(f => f.GrantId).Order());
        Assert.All(findings, f => Assert.Contains("mer enn 100", f.Detail));
    }

    [Fact]
    public void InvalidShares_MissingShareWhenSeveralGrants()
    {
        var a = Make("A", GrantStatus.Granted, GrantTestData.Kurs);
        var b = Make("B", GrantStatus.Granted, GrantTestData.Kurs);
        b.ProjectShares = new() { [GrantTestData.Kurs] = 40m };

        var findings = Build([a, b]).Findings.Where(f => f.Kind == GrantCheckKind.InvalidShares).ToList();

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Contains("fordelingsnøkkel", f.Detail));
    }

    [Fact]
    public void NoFikenData_StillChecksDeadlines_AndExplainsWhatWasSkipped()
    {
        var grant = Make("Kurs", GrantStatus.Submitted);
        grant.ReportDeadline = Ref.AddDays(5);

        var section = GrantCheckRules.Build([grant], GrantFikenData.None, Ref, "Fiken svarte ikke.");

        Assert.Single(section.Findings);
        Assert.StartsWith("Mangler Fiken-data: Fiken svarte ikke.", section.MissingDataNote);
        Assert.Equal(1, section.GrantCount);
    }

    [Fact]
    public void NoGrants_IsOk()
    {
        var section = GrantCheckRules.Build([], GrantFikenData.None, Ref);

        Assert.Empty(section.Findings);
        Assert.Equal(0, section.GrantCount);
        Assert.Null(section.MissingDataNote);
        Assert.Equal(QualityStatus.Ok, section.Status);
    }

    [Fact]
    public async Task DateScope_UsedIgnoresCostsAfterReferenceDate()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 1000;
        var data = await LoadAsync([grant], EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-07-01"));

        Assert.DoesNotContain(Build([grant], data, Ref).Findings, f => f.Kind == GrantCheckKind.UsedExceedsGranted);
        Assert.Contains(Build([grant], data, new DateOnly(2027, 7, 1)).Findings, f => f.Kind == GrantCheckKind.UsedExceedsGranted);
    }

    [Fact]
    public void DateScope_ManualUseAndManualPaymentAfterReferenceDateAreIgnored()
    {
        var grant = new Grant
        {
            Name = "Drift",
            Kind = GrantKind.Operating,
            Status = GrantStatus.Granted,
            GrantedAmount = 1000,
            DecisionDate = Ref.AddDays(-90),
            ManualUses = [new GrantManualUse { Date = Ref.AddDays(1), Amount = 5000 }],
            Payments = [new GrantPayment { Date = Ref.AddDays(1), Amount = 1000 }]
        };

        var findings = Build([grant]).Findings;

        Assert.DoesNotContain(findings, f => f.Kind == GrantCheckKind.UsedExceedsGranted);
        Assert.Contains(findings, f => f.Kind == GrantCheckKind.NotReceived);
        Assert.DoesNotContain(Build([grant], date: Ref.AddDays(1)).Findings, f => f.Kind == GrantCheckKind.NotReceived);
    }

    [Fact]
    public async Task DateScope_LinkedPaymentAfterReferenceDate_IsNotReportedMissing_ButTrulyMissingIs()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 10000;
        grant.DecisionDate = Ref.AddDays(-90);
        grant.Payments.Add(new GrantPayment { JournalEntryId = 2, Date = new DateOnly(2027, 7, 1) });
        grant.Payments.Add(new GrantPayment { JournalEntryId = 999, Date = new DateOnly(2027, 3, 1) });
        var data = await LoadAsync([grant], EntryJson(2, "3400", -1000000, null, "2027-07-01"));

        var findings = Build([grant], data).Findings.Where(f => f.Kind == GrantCheckKind.PaymentNotFound).ToList();

        var finding = Assert.Single(findings);
        Assert.Contains("999", finding.Detail);
    }

    [Fact]
    public async Task DateScope_LinkedPaymentAfterReferenceDate_IsNotCountedAsReceived()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.GrantedAmount = 10000;
        grant.DecisionDate = Ref.AddDays(-90);
        grant.Payments.Add(new GrantPayment { JournalEntryId = 2, Date = new DateOnly(2027, 7, 1) });
        var data = await LoadAsync([grant], EntryJson(2, "3400", -1000000, null, "2027-07-01"));

        var finding = Assert.Single(Build([grant], data).Findings, f => f.Kind == GrantCheckKind.NotReceived);

        Assert.Equal(10000m, finding.Amount);
        Assert.DoesNotContain(Build([grant], data, new DateOnly(2027, 7, 2)).Findings, f => f.Kind == GrantCheckKind.NotReceived);
    }

    [Fact]
    public async Task ClosedAndReported_SkipCostsOutsidePeriod_ClosedSkipsUsedExceedsGranted()
    {
        var closed = Make("Avsluttet", GrantStatus.Closed, GrantTestData.Kurs);
        closed.GrantedAmount = 100;
        closed.UseFrom = new DateOnly(2027, 2, 1);
        var reported = Make("Rapportert", GrantStatus.Reported, GrantTestData.Leir);
        reported.GrantedAmount = 100;
        reported.UseFrom = new DateOnly(2027, 2, 1);
        var data = await LoadAsync([closed, reported],
            EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-03-01"),
            EntryJson(2, "6800", 150000, GrantTestData.Kurs, "2027-01-10"),
            EntryJson(3, "6800", 150000, GrantTestData.Leir, "2027-03-01"),
            EntryJson(4, "6800", 150000, GrantTestData.Leir, "2027-01-10"));

        var findings = Build([closed, reported], data).Findings;

        Assert.DoesNotContain(findings, f => f.Kind == GrantCheckKind.CostsOutsidePeriod);
        Assert.DoesNotContain(findings, f => f.GrantId == closed.Id && f.Kind == GrantCheckKind.UsedExceedsGranted);
        Assert.Contains(findings, f => f.GrantId == reported.Id && f.Kind == GrantCheckKind.UsedExceedsGranted);
    }

    [Fact]
    public async Task CostsOutsidePeriod_IsWeightedByShare_AndMentionsIt()
    {
        var grant = Make("Kurs", GrantStatus.Granted, GrantTestData.Kurs);
        grant.UseFrom = new DateOnly(2027, 2, 1);
        grant.ProjectShares = new() { [GrantTestData.Kurs] = 40m };
        var data = await LoadAsync([grant], EntryJson(1, "6800", 100000, GrantTestData.Kurs, "2027-01-15"));

        var finding = Assert.Single(Build([grant], data).Findings, f => f.Kind == GrantCheckKind.CostsOutsidePeriod);

        Assert.Equal(400m, finding.Amount);
        Assert.Contains("andel (40 %)", finding.Detail);
    }

    [Fact]
    public void FailedSection_ShowsErrorInStatusAndFormats()
    {
        var section = GrantCheckSection.Failed("Lesefeil i grants.json.");
        var report = new QualityCheckReport(Ref, DateTimeOffset.UnixEpoch, [], section);

        Assert.Equal(QualityStatus.CheckManually, section.Status);
        foreach (var text in new[] { QualityCheckTextFormatter.ToTabSeparated(report), QualityCheckTextFormatter.ToMarkdown(report), QualityCheckTextFormatter.ToHtml(report) })
        {
            Assert.Contains("kunne ikke kontrolleres", text);
            Assert.Contains("Lesefeil i grants.json.", text);
            Assert.DoesNotContain("Ingen funn.", text);
        }
    }

    [Fact]
    public void UnusedOrNull_IsNullWhenReceivedOrUsedUnknown()
    {
        var known = Make("Kjent", GrantStatus.Granted);
        known.Payments.Add(new GrantPayment { Amount = 500, Date = Ref });
        known.ManualUses = [];
        var report = GrantReportBuilder.Build(known, [known], null);
        Assert.Null(GrantOverviewLogic.UnusedOrNull(report));

        var operating = new Grant { Name = "D", Kind = GrantKind.Operating, Payments = [new GrantPayment { Amount = 500 }], ManualUses = [new GrantManualUse { Amount = 200 }] };
        Assert.Equal(300m, GrantOverviewLogic.UnusedOrNull(GrantReportBuilder.Build(operating, [operating], null)));

        var linked = new Grant { Name = "L", Kind = GrantKind.Operating, Payments = [new GrantPayment { JournalEntryId = 5 }], ManualUses = [new GrantManualUse { Amount = 200 }] };
        Assert.Null(GrantOverviewLogic.UnusedOrNull(GrantReportBuilder.Build(linked, [linked], null)));
    }

    [Fact]
    public void Report_IncludesGrantSectionInStatusAndFormats()
    {
        var grant = Make("Lederkurs 2027", GrantStatus.Submitted);
        grant.ReportDeadline = Ref.AddDays(-3);
        grant.GrantedAmount = 12345.5m;
        grant.DecisionDate = Ref.AddDays(-100);
        grant.Status = GrantStatus.Granted;
        var section = Build([grant]);
        var report = new QualityCheckReport(Ref, DateTimeOffset.UnixEpoch, [], section);

        Assert.Equal(QualityStatus.Overdue, report.Status);

        var tab = QualityCheckTextFormatter.ToTabSeparated(report);
        Assert.Contains("Tilskudd: 2 funn", tab);
        Assert.Contains("Lederkurs 2027\tRapporteringsfrist er passert", tab);
        Assert.Contains("\t12345,50", tab);

        var md = QualityCheckTextFormatter.ToMarkdown(report);
        Assert.Contains("### Tilskudd: 2 funn", md);
        Assert.Contains("| Status | Tilskudd | Kontroll | Detalj | Beløp (kr) |", md);
        Assert.Contains("12 345,50", md);

        var html = QualityCheckTextFormatter.ToHtml(report);
        Assert.Contains("<h3>Tilskudd: 2 funn", html);
        Assert.Contains("Lederkurs 2027", html);
    }

    [Fact]
    public void Formats_ShowMissingDataNoteAndEmptyResult()
    {
        var section = GrantCheckRules.Build([Make("Kurs", GrantStatus.Submitted)], GrantFikenData.None, Ref);
        var report = new QualityCheckReport(Ref, DateTimeOffset.UnixEpoch, [], section);

        Assert.Contains("Tilskudd: ingen funn", QualityCheckTextFormatter.ToTabSeparated(report));
        Assert.Contains("Mangler Fiken-data", QualityCheckTextFormatter.ToMarkdown(report));
        Assert.Contains("Mangler Fiken-data", QualityCheckTextFormatter.ToHtml(report));
        Assert.Contains("Ingen funn.", QualityCheckTextFormatter.ToHtml(report));
    }

    [Fact]
    public void ReportWithoutGrants_IsUnchanged()
    {
        var report = new QualityCheckReport(Ref, DateTimeOffset.UnixEpoch, []);

        Assert.DoesNotContain("Tilskudd", QualityCheckTextFormatter.ToMarkdown(report));
        Assert.Equal(QualityStatus.Ok, report.Status);
    }
}

public sealed class GrantProjectLinksTests
{
    [Fact]
    public void Build_MapsProjectsToActiveProjectGrants_WithShareDefaultingTo100()
    {
        var a = GrantTestData.Project("Zebra", GrantTestData.Kurs);
        var b = GrantTestData.Project("Alfa", GrantTestData.Kurs, GrantTestData.Leir);
        b.ProjectShares = new() { [GrantTestData.Kurs] = 25.5m };
        var rejected = GrantTestData.Project("Avslått", GrantTestData.Leir);
        rejected.Status = GrantStatus.Rejected;
        var withdrawn = GrantTestData.Project("Trukket", GrantTestData.Leir);
        withdrawn.Status = GrantStatus.Withdrawn;
        var operating = new Grant { Name = "Drift", Kind = GrantKind.Operating };

        var map = GrantProjectLinks.Build([a, b, rejected, withdrawn, operating]);

        var kurs = map[GrantTestData.Kurs];
        Assert.Equal(["Alfa", "Zebra"], kurs.Select(l => l.Name));
        Assert.Equal(25.5m, kurs[0].SharePercent);
        Assert.Equal(100m, kurs[1].SharePercent);
        Assert.Equal("Tilskudd: Alfa (25,5 %)", kurs[0].Tooltip);
        Assert.Equal($"tilskudd/{b.Id}", kurs[0].Href);
        var leir = Assert.Single(map[GrantTestData.Leir]);
        Assert.Equal("Alfa", leir.Name);
        Assert.Equal(100m, leir.SharePercent);
        Assert.False(map.ContainsKey(1));
    }
}

public sealed class GrantToolTests : GrantFikenTestBase
{
    private GrantTools CreateTools(string slug = "test-as", bool withFiken = true)
    {
        if (withFiken)
        {
            Api.Get("companies/test-as/projects", $$"""[{"projectId":{{GrantTestData.Kurs}},"name":"Lederkurs 2027","completed":false}]""")
                .Get("companies/test-as/accounts", """[{"code":"6800","name":"Kontorrekvisita"},{"code":"6900","name":"Telefon"}]""")
                .Get("companies/test-as/journalEntries", "[" + string.Join(",",
                    EntryJson(1, "6800", 150000, GrantTestData.Kurs, "2027-03-01"),
                    EntryJson(2, "6900", 30000, GrantTestData.Kurs, "2027-03-02")) + "]");
        }

        var time = new ManualTimeProvider(new DateTimeOffset(2027, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var store = Api.CreateStore(slug);
        var client = Api.CreateClient(store);
        var cache = new FikenDataCache(time);
        var reports = new BudgetReportService(client, new JsonBudgetStore(Dir), store, cache);
        var service = new GrantService(new JsonGrantStore(Dir), reports, client, cache, time);
        return new GrantTools(service, new FikenContextService(store, client, cache), time);
    }

    private async Task SaveAsync(params Grant[] grants)
    {
        var errors = await new JsonGrantStore(Dir).UpdateAsync(data =>
        {
            data.Grants.AddRange(grants);
            return [];
        }, TestContext.Current.CancellationToken);
        Assert.Empty(errors);
    }

    private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value, ToolSupport.JsonOptions);

    private static Grant Kurs()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        grant.Grantor = "Eksempel kommune";
        grant.GrantedAmount = 1000;
        grant.DecisionDate = new DateOnly(2027, 1, 10);
        grant.ReportDeadline = new DateOnly(2027, 6, 15);
        return grant;
    }

    [Fact]
    public async Task GetGrantStatus_ReturnsNumbersTopAccountsAndFindings_AndOnlyReads()
    {
        await SaveAsync(Kurs());
        var tools = CreateTools();

        var json = ToJson(await tools.GetGrantStatus(topAccounts: 1, ct: TestContext.Current.CancellationToken));

        var grant = json.GetProperty("grants")[0];
        Assert.Equal("Lederkurs 2027", grant.GetProperty("name").GetString());
        Assert.Equal("Innvilget", grant.GetProperty("status").GetString());
        Assert.Equal(1800m, grant.GetProperty("used").GetDecimal());
        Assert.Equal("ok", grant.GetProperty("usedStatus").GetString());
        Assert.Equal(1000m, grant.GetProperty("remainingToReceive").GetDecimal());
        var accounts = grant.GetProperty("usedByAccount");
        Assert.Equal(1, accounts.GetArrayLength());
        Assert.Equal(6800, accounts[0].GetProperty("account").GetInt32());
        Assert.Equal(2, grant.GetProperty("usedByAccountCount").GetInt32());
        var checks = grant.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("check").GetString()).ToList();
        Assert.Contains("Brukt mer enn innvilget", checks);
        Assert.Contains("Rapporteringsfrist nærmer seg", checks);
        Assert.Equal(1, grant.GetProperty("deadlines").GetArrayLength());
        Assert.True(json.GetProperty("hasFikenData").GetBoolean());
        Assert.All(Api.Calls, c => Assert.Equal(HttpMethod.Get, c.Method));
    }

    [Fact]
    public async Task GetGrantStatus_WithoutFiken_SaysMissingInsteadOfZero()
    {
        await SaveAsync(Kurs());
        var tools = CreateTools(slug: "", withFiken: false);

        var json = ToJson(await tools.GetGrantStatus(ct: TestContext.Current.CancellationToken));

        var grant = json.GetProperty("grants")[0];
        Assert.False(json.GetProperty("hasFikenData").GetBoolean());
        Assert.Contains("Mangler Fiken-data", json.GetProperty("missingFikenData").GetString());
        Assert.Equal("missingFikenData", grant.GetProperty("usedStatus").GetString());
        Assert.False(grant.TryGetProperty("used", out _));
        Assert.Contains(grant.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("Brukt: mangler Fiken-data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetGrantStatus_UnregisteredUse_IsNotRegisteredNotZero()
    {
        var grant = new Grant { Name = "Søknad", Grantor = "Stiftelse", Status = GrantStatus.Submitted };
        await SaveAsync(grant);
        var tools = CreateTools(withFiken: false);

        var json = ToJson(await tools.GetGrantStatus(ct: TestContext.Current.CancellationToken));

        var row = json.GetProperty("grants")[0];
        Assert.Equal("notRegistered", row.GetProperty("usedStatus").GetString());
        Assert.False(row.TryGetProperty("used", out _));
        Assert.False(row.TryGetProperty("granted", out _));
        Assert.Contains(row.GetProperty("notes").EnumerateArray(), n => n.GetString() == "Innvilget beløp er ikke registrert.");
    }

    [Fact]
    public async Task GetGrantStatus_FiltersOnStatusNameIdAndYear()
    {
        var a = Kurs();
        var b = new Grant { Name = "Leirtilskudd", Grantor = "Stiftelsen Eksempel", Status = GrantStatus.Rejected, AppliedDate = new DateOnly(2026, 3, 1) };
        await SaveAsync(a, b);
        var tools = CreateTools(withFiken: false);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(2, ToJson(await tools.GetGrantStatus(ct: ct)).GetProperty("count").GetInt32());
        Assert.Equal("Leirtilskudd", ToJson(await tools.GetGrantStatus(status: "avslått", ct: ct)).GetProperty("grants")[0].GetProperty("name").GetString());
        Assert.Equal("Lederkurs 2027", ToJson(await tools.GetGrantStatus(name: "kommune", ct: ct)).GetProperty("grants")[0].GetProperty("name").GetString());
        Assert.Equal("Lederkurs 2027", ToJson(await tools.GetGrantStatus(grantId: a.Id.ToString(), ct: ct)).GetProperty("grants")[0].GetProperty("name").GetString());
        Assert.Equal(1, ToJson(await tools.GetGrantStatus(year: 2026, ct: ct)).GetProperty("count").GetInt32());
        Assert.Equal(0, ToJson(await tools.GetGrantStatus(name: "finnes ikke", ct: ct)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetGrantStatus_EmptySelection_HasNoFalseMissingDataAndNoFikenCalls()
    {
        await SaveAsync(Kurs());
        var tools = CreateTools(withFiken: false);

        var json = ToJson(await tools.GetGrantStatus(name: "finnes ikke", ct: TestContext.Current.CancellationToken));

        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.False(json.TryGetProperty("missingFikenData", out _));
        Assert.Contains("navn «finnes ikke»", json.GetProperty("filter").GetString());
        Assert.Empty(Api.Calls);
    }

    [Fact]
    public async Task GetGrantStatus_UnusedIsOmittedWhenReceivedOrUsedUnknown_AndAccountsSortedByAbsolute()
    {
        var grant = Kurs();
        grant.Payments.Add(new GrantPayment { Amount = 500, Date = new DateOnly(2027, 2, 1) });
        await SaveAsync(grant);

        var withoutFiken = ToJson(await CreateTools(withFiken: false).GetGrantStatus(ct: TestContext.Current.CancellationToken));
        Assert.False(withoutFiken.GetProperty("grants")[0].TryGetProperty("unused", out _));

        var withFiken = ToJson(await CreateTools().GetGrantStatus(ct: TestContext.Current.CancellationToken));
        Assert.Equal(-1300m, withFiken.GetProperty("grants")[0].GetProperty("unused").GetDecimal());
    }

    [Fact]
    public async Task GetGrantStatus_FikenTimeout_FallsBackToLocalGrants()
    {
        await SaveAsync(Kurs());
        Api.On(HttpMethod.Get, "companies/test-as/projects", (_, _) => throw new TaskCanceledException("tidsavbrudd", new TimeoutException()));
        var tools = CreateTools(withFiken: false);

        var json = ToJson(await tools.GetGrantStatus(ct: TestContext.Current.CancellationToken));

        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.False(json.GetProperty("hasFikenData").GetBoolean());
        Assert.Contains("Mangler Fiken-data", json.GetProperty("missingFikenData").GetString());
    }

    [Fact]
    public async Task GetGrantStatus_RejectsBadInput()
    {
        var tools = CreateTools(withFiken: false);
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<McpException>(() => tools.GetGrantStatus(status: "tull", ct: ct));
        await Assert.ThrowsAsync<McpException>(() => tools.GetGrantStatus(grantId: "ikke-guid", ct: ct));
        await Assert.ThrowsAsync<McpException>(() => tools.GetGrantStatus(year: 1800, ct: ct));
    }
}

public sealed class GrantStoreLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fh-grantlock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAndUpdateFromTwoStoreInstances_DoNotDeadlock_AndReadsAreConsistent()
    {
        // To instanser mot samme mappe tilsvarer Web og MCP (to prosesser): bare fillåsen deles.
        var writer = new JsonGrantStore(_dir);
        var reader = new JsonGrantStore(_dir);
        var ct = TestContext.Current.CancellationToken;

        var work = Task.WhenAll(
            Task.Run(async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    var errors = await writer.UpdateAsync(d => { d.Grants.Add(new Grant { Name = $"Tilskudd {i}" }); return []; }, ct);
                    Assert.Empty(errors);
                }
            }, ct),
            Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    var data = await reader.LoadAsync(ct);
                    Assert.InRange(data.Grants.Count, 0, 25);
                }
            }, ct),
            Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    await writer.LoadAsync(ct);
                }
            }, ct));

        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30), ct));
        Assert.Same(work, finished);
        await work;
        Assert.Equal(25, (await reader.LoadAsync(ct)).Grants.Count);
    }

    [Fact]
    public async Task LoadWaitsWhileAnotherProcessHoldsTheLock()
    {
        var store = new JsonGrantStore(_dir);
        var ct = TestContext.Current.CancellationToken;
        using var held = await FikenHjelper.Core.Config.CrossProcessFileLock.AcquireAsync(Path.Combine(_dir, "grants.lock"), ct);

        var load = store.LoadAsync(ct);
        await Task.Delay(300, ct);
        Assert.False(load.IsCompleted);

        held.Dispose();
        Assert.Empty((await load).Grants);
    }
}
