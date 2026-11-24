using FikenHjelper.Core.Analysis;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Tests;

public sealed class ProjectVarianceExplainerTests
{
    private static FikenJournalEntry Entry(long id, string date, params FikenJournalEntryLine[] lines) =>
        new(id, null, null, (int)id, $"Bilag {id}", date, lines);

    private static FikenJournalEntryLine Line(long amountOre, string account, params long[] projects) =>
        new(amountOre, account, null, projects);

    [Fact]
    public void SumsMatchBudgetReportBuilderAndBreakDownPerAccount()
    {
        var entries = new[]
        {
            Entry(1, "2026-03-01", Line(-100000, "3200", 7), Line(100000, "1920")),
            Entry(2, "2026-04-01", Line(30000, "6800", 7), Line(-30000, "2400:42")),
            Entry(3, "2026-05-01", Line(20000, "6800", 7, 8), Line(-20000, "1920")),
            Entry(4, "2026-05-02", Line(5000, "6800", 8), Line(-5000, "1920")),
            Entry(5, "2026-05-03", Line(9900, "8800", 7), Line(-9900, "2050"))
        };
        var projects = new[] { new FikenProject(7, "7", "Landsleir", null, null, null, false) };

        var explanation = ProjectVarianceExplainer.Explain(2026, [7], entries, null, [new FikenAccount("6800", "Kontorrekvisita")], 10);

        var report = BudgetReportBuilder.Build(new BudgetYear { Year = 2026, Lines = [new BudgetLine { Name = "Leir", ProjectIds = [7] }] }, projects, entries);
        Assert.Equal(report.Lines[0].Actual, explanation.Actual);
        Assert.Equal(500m, explanation.Actual); // 1000 inntekt − 300 − 200 (8800 holdes utenfor)
        Assert.Equal(3, explanation.PostingCount);

        var cost = Assert.Single(explanation.ByAccount, a => a.Account == "6800");
        Assert.Equal(-500m, cost.Amount);
        Assert.Equal("Kontorrekvisita", cost.AccountName);
        Assert.Equal(1000m, explanation.LargestPostings[0].Amount);
    }

    [Fact]
    public void LimitsPostingsAndHandlesNoMatches()
    {
        var entries = Enumerable.Range(1, 10).Select(i => Entry(i, "2026-01-01", Line(-100 * i, "3200", 1))).ToList();
        var explanation = ProjectVarianceExplainer.Explain(2026, [1], entries, null, [], 3);
        Assert.Equal(3, explanation.LargestPostings.Count);
        Assert.Equal(10m, explanation.LargestPostings[0].Amount);

        var none = ProjectVarianceExplainer.Explain(2026, [99], entries, null, [], 3);
        Assert.Equal(0, none.PostingCount);
        Assert.Equal(0m, none.Actual);
    }
}

public sealed class AnomalyFinderTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);
    private static readonly FikenContact Rema = new(42, "Rema", null, null, null, null, false, true, false, null, null);

    [Fact]
    public void OverduePurchases_OnlyUnpaidPastDueDate()
    {
        var purchases = new[]
        {
            new FikenPurchase(1, null, "A", "2026-10-01", "2026-10-15", "supplier", false, [new FikenOrderLine(null, "x", 10000, 0, "6800", "NONE", null)], Rema, "NOK", null, null, false, false),
            new FikenPurchase(2, null, "B", "2026-11-20", "2026-12-04", "supplier", false, null, Rema, "NOK", null, null, false, false),
            new FikenPurchase(3, null, "C", "2026-10-01", "2026-10-15", "supplier", true, null, Rema, "NOK", null, null, false, true),
            new FikenPurchase(4, null, "D", "2026-10-01", "2026-10-15", "supplier", false, [new FikenOrderLine(null, "x", 10000, 0, "6800", "NONE", null)], Rema, "NOK", [new FikenPayment(1, "2026-10-10", 10000)], null, false, false)
        };

        var anomaly = Assert.Single(AnomalyFinder.OverduePurchases(purchases, Today));
        Assert.Equal("kjøp 1", anomaly.Reference);
        Assert.Equal(100m, anomaly.AmountKr);
        Assert.Equal(AnomalyFinder.High, anomaly.Severity);
    }

    [Fact]
    public void OverdueSales_UseOutstandingBalance()
    {
        var sales = new[]
        {
            new FikenSale(1, "10", "2026-09-01", "2026-09-15", "invoice", 45000, 0, false, 45000, null, false),
            new FikenSale(2, "11", "2026-09-01", "2026-09-15", "invoice", 45000, 0, false, 0, null, false)
        };
        var anomaly = Assert.Single(AnomalyFinder.OverdueSales(sales, Today));
        Assert.Equal(450m, anomaly.AmountKr);
    }

    [Fact]
    public void StaleInboxAndDrafts_RespectAgeLimits()
    {
        var inbox = new[]
        {
            new FikenInboxDocument(1, "Gammel", null, "a.pdf", false, "2026-10-01T10:00:00Z", null),
            new FikenInboxDocument(2, "Ny", null, "b.pdf", false, "2026-11-20T10:00:00Z", null)
        };
        var ehf = new[]
        {
            new FikenEhfDocument(9, "unprocessed", "invoice", "E-1", "2026-10-01", "2026-10-20", "Rema", null, 42, 1000, 0, 1000, null, "NOK", null, null, null, null, null, "2026-10-02T08:00:00Z", null)
        };
        var stale = AnomalyFinder.StaleInbox(inbox, ehf, Today, 14).ToList();
        Assert.Equal(2, stale.Count);
        Assert.Contains(stale, a => a.Reference == "innboks 1");
        Assert.Contains(stale, a => a.Reference == "ehf 9" && a.Severity == AnomalyFinder.High);

        var drafts = new[]
        {
            new FikenPurchaseDraft(5, null, "2026-09-01", null, "F", Rema, false, null, null),
            new FikenPurchaseDraft(6, null, "2026-11-20", null, "G", Rema, false, null, null)
        };
        Assert.Equal("kjøpsutkast 5", Assert.Single(AnomalyFinder.StaleDrafts(drafts, Today, 30)).Reference);
    }

    [Fact]
    public void PurchasesWithoutAttachment_AndPostingsAfterProjectEnd()
    {
        var purchases = new[]
        {
            new FikenPurchase(1, null, "A", "2026-01-01", null, "supplier", true, null, Rema, "NOK", null, [], false, true),
            new FikenPurchase(2, null, "B", "2026-01-01", null, "supplier", true, null, Rema, "NOK", null, [new FikenAttachment("u", "a.pdf", null, "invoice")], false, true)
        };
        Assert.Equal("kjøp 1", Assert.Single(AnomalyFinder.PurchasesWithoutAttachment(purchases)).Reference);

        var entries = new[]
        {
            new FikenJournalEntry(1, null, null, 1, "Etter", "2026-03-01", [new FikenJournalEntryLine(1000, "6800", null, [3])]),
            new FikenJournalEntry(2, null, null, 2, "Før", "2025-06-01", [new FikenJournalEntryLine(1000, "6800", null, [3])])
        };
        var projects = new[] { new FikenProject(3, "3", "Landsleir 2025", null, "2025-01-01", "2025-12-31", true) };
        var after = Assert.Single(AnomalyFinder.PostingsAfterProjectEnd(entries, projects));
        Assert.Equal("bilag 1", after.Reference);
    }

    [Fact]
    public void FromBudgetReport_ListsUnallocatedUnlinkedAndDoubleLinked()
    {
        var entries = new[]
        {
            new FikenJournalEntry(1, null, null, 1, "Uten prosjekt", "2026-01-01", [new FikenJournalEntryLine(1000, "6800", null, [])]),
            new FikenJournalEntry(2, null, null, 2, "Ukoblet", "2026-01-01", [new FikenJournalEntryLine(1000, "6800", null, [9])])
        };
        var budget = new BudgetYear
        {
            Year = 2026,
            Lines = [new BudgetLine { Name = "A", ProjectIds = [7] }, new BudgetLine { Name = "B", ProjectIds = [7] }]
        };
        var report = BudgetReportBuilder.Build(budget, [new FikenProject(9, null, "Ni", null, null, null, false)], entries);

        var types = AnomalyFinder.FromBudgetReport(report).Select(a => a.Type).ToList();
        Assert.Contains(AnomalyTypes.UnallocatedPostings, types);
        Assert.Contains(AnomalyTypes.UnlinkedProjects, types);
        Assert.Contains(AnomalyTypes.ProjectsLinkedToSeveralLines, types);
    }
}
