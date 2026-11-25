using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Purchasing;

namespace FikenHjelper.Tests;

public sealed class PurchaseDraftValidatorTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);

    private static readonly FikenContact Supplier = new(42, "Rema 1000 Molde", null, "987654321", null, null, false, true, false, null, null);

    private static PurchaseDraftContext Context(
        FikenEhfDocument? ehf = null,
        FikenContact? supplier = null,
        IReadOnlyList<FikenPurchaseDraft>? drafts = null,
        IReadOnlyList<FikenPurchase>? purchases = null) => new(
        [new FikenAccount("6800", "Kontorrekvisita"), new FikenAccount("6540", "Inventar"), new FikenAccount("1920:10001", "Bank")],
        [
            new FikenProject(7, "7", "Landsleir 2026", null, "2026-01-01", null, false),
            new FikenProject(3, "3", "Landsleir 2025", null, "2025-01-01", "2025-12-31", true)
        ],
        drafts ?? [],
        purchases ?? [],
        supplier,
        ehf,
        null,
        PurchaseDraftValidator.DefaultVatType,
        Today);

    private static PurchaseDraftInput Input(params PurchaseDraftLineInput[] lines) => new(
        PurchaseDocumentSource.Inbox(11), 42, "F-1", new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 4), null, lines);

    [Fact]
    public void ValidDraft_ConvertsKronerToOreWithNetEqualGrossAndNoneVat()
    {
        var result = PurchaseDraftValidator.Validate(
            Input(new PurchaseDraftLineInput(" Mat til leir ", "6800", 1249.50m, 7)),
            Context(supplier: Supplier));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var line = Assert.Single(result.Request!.Lines);
        Assert.Equal(124950, line.Net);
        Assert.Equal(124950, line.Gross);
        Assert.Equal("NONE", line.VatType);
        Assert.Equal("Mat til leir", line.Text);
        Assert.Equal(7, line.ProjectId);
        Assert.False(result.Request.Paid);
        Assert.False(result.Request.Cash);
        Assert.Equal("2026-11-20", result.Request.InvoiceIssueDate);
        Assert.Equal(1249.50m, result.TotalKr);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(10.555)]
    public void InvalidAmounts_AreErrors(decimal amount)
    {
        var result = PurchaseDraftValidator.Validate(Input(new PurchaseDraftLineInput("x", "6800", amount, null)), Context(supplier: Supplier));
        Assert.False(result.IsValid);
        Assert.Null(result.Request);
        Assert.Contains(result.Errors, e => e.Contains("beløpet"));
    }

    [Fact]
    public void UnknownAccountAndProject_AreErrors_CompletedProjectIsWarning_BalanceAccountIsError()
    {
        var unknown = PurchaseDraftValidator.Validate(
            Input(new PurchaseDraftLineInput("x", "6999", 10, 999)),
            Context(supplier: Supplier));
        Assert.Contains(unknown.Errors, e => e.Contains("6999 finnes ikke"));
        Assert.Contains(unknown.Errors, e => e.Contains("prosjekt 999"));

        var warnings = PurchaseDraftValidator.Validate(
            Input(new PurchaseDraftLineInput("x", "6800", 10, 3)),
            Context(supplier: Supplier));
        Assert.True(warnings.IsValid);
        Assert.Contains(warnings.Warnings, w => w.Contains("avsluttet"));

        var balance = PurchaseDraftValidator.Validate(
            Input(new PurchaseDraftLineInput("x", "1920:10001", 10, null)),
            Context(supplier: Supplier));
        Assert.False(balance.IsValid);
        Assert.Contains(balance.Errors, e => e.Contains("balanse- eller inntektskonto"));
    }

    [Fact]
    public void VatTypeOtherThanNone_IsError()
    {
        var context = Context(supplier: Supplier) with { VatType = "HIGH" };
        var result = PurchaseDraftValidator.Validate(Input(new PurchaseDraftLineInput("x", "6800", 10, null)), context);
        Assert.Contains(result.Errors, e => e.Contains("HIGH"));
    }

    [Fact]
    public void SupplierDifferentFromEhf_GivesWarning()
    {
        var ehf = new FikenEhfDocument(22, "unprocessed", "invoice", "EHF-9", "2026-11-10", null, "Rema", null, 77,
            1000, 0, 1000, null, "NOK", null, null, null, null, null, null, null);
        var result = PurchaseDraftValidator.Validate(
            new PurchaseDraftInput(PurchaseDocumentSource.Ehf(22), 42, null, null, null, null, [new PurchaseDraftLineInput("x", "6800", 10, null)]),
            Context(ehf: ehf, supplier: Supplier));
        Assert.Contains(result.Warnings, w => w.Contains("kontakt 77"));
    }

    [Fact]
    public void EhfSource_FillsHeaderFromDocumentAndRequiresMatchingTotal()
    {
        var ehf = new FikenEhfDocument(22, "unprocessed", "invoice", "EHF-9", "2026-11-10", "2026-11-24", "Rema", "987654321", 42,
            10000, 0, 10000, 10000, "NOK", "12345678", null, null, null, null, null, null);
        var input = new PurchaseDraftInput(PurchaseDocumentSource.Ehf(22), null, null, null, null, null,
            [new PurchaseDraftLineInput("Mat", "6800", 60, 7), new PurchaseDraftLineInput("Mat", "6800", 40, null)]);

        var ok = PurchaseDraftValidator.Validate(input, Context(ehf: ehf, supplier: Supplier));
        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));
        Assert.Equal(42, ok.Request!.ContactId);
        Assert.Equal("EHF-9", ok.Request.InvoiceNumber);
        Assert.Equal("2026-11-10", ok.Request.InvoiceIssueDate);
        Assert.Equal("2026-11-24", ok.Request.DueDate);
        Assert.Equal("12345678", ok.Request.Kid);

        var mismatch = PurchaseDraftValidator.Validate(
            input with { Lines = [new PurchaseDraftLineInput("Mat", "6800", 99.99m, 7)] },
            Context(ehf: ehf, supplier: Supplier));
        Assert.Contains(mismatch.Errors, e => e.Contains("ikke lik EHF-fakturaens brutto"));
    }

    [Fact]
    public void CreditNoteAndForeignCurrency_AreRejected()
    {
        var ehf = new FikenEhfDocument(22, "unprocessed", "creditNote", "K-1", "2026-11-10", null, "Rema", null, 42,
            10000, 0, 10000, null, "SEK", null, null, null, null, null, null, null);
        var result = PurchaseDraftValidator.Validate(
            new PurchaseDraftInput(PurchaseDocumentSource.Ehf(22), null, null, null, null, null, [new PurchaseDraftLineInput("x", "6800", 100, null)]),
            Context(ehf: ehf, supplier: Supplier));

        Assert.Contains(result.Errors, e => e.Contains("kreditnota"));
        Assert.Contains(result.Errors, e => e.Contains("SEK"));
    }

    [Fact]
    public void DuplicateInvoiceNumber_InDraftsOrBookedPurchases_GivesWarnings()
    {
        var drafts = new[] { new FikenPurchaseDraft(5, null, null, null, "F-1", Supplier, false, null, null) };
        var purchases = new[] { new FikenPurchase(8, null, "F-1", "2026-11-01", null, "supplier", false, null, Supplier, "NOK", null, null, false, false) };

        var result = PurchaseDraftValidator.Validate(
            Input(new PurchaseDraftLineInput("x", "6800", 10, null)),
            Context(supplier: Supplier, drafts: drafts, purchases: purchases));

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("utkast 5"));
        Assert.Contains(result.Warnings, w => w.Contains("kjøp 8"));
    }

    [Fact]
    public void MissingSupplierAndAttachment_AreWarnings_InvalidKidAndDatesAreErrors()
    {
        var noSupplier = PurchaseDraftValidator.Validate(
            new PurchaseDraftInput(null, null, null, new DateOnly(2026, 11, 1), null, null, [new PurchaseDraftLineInput("x", "6800", 10, null)]),
            Context());
        Assert.True(noSupplier.IsValid);
        Assert.Contains(noSupplier.Warnings, w => w.Contains("Ingen leverandør"));
        Assert.Contains(noSupplier.Warnings, w => w.Contains("ikke vedlegg"));
        Assert.Null(noSupplier.Request!.ContactId);

        var bad = PurchaseDraftValidator.Validate(
            new PurchaseDraftInput(null, null, null, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 1), "12a", [new PurchaseDraftLineInput("x", "6800", 10, null)]),
            Context());
        Assert.Contains(bad.Errors, e => e.Contains("KID"));
        Assert.Contains(bad.Errors, e => e.Contains("Forfallsdato"));
    }

    [Fact]
    public void UnknownSupplierId_IsError()
    {
        var result = PurchaseDraftValidator.Validate(Input(new PurchaseDraftLineInput("x", "6800", 10, null)), Context(supplier: null));
        Assert.Contains(result.Errors, e => e.Contains("Fant ikke leverandør"));
    }
}

public sealed class PurchaseSuggestionBuilderTests
{
    private static FikenPurchase Purchase(long id, string date, params FikenOrderLine[] lines) =>
        new(id, null, $"F-{id}", date, null, "supplier", true, lines, null, "NOK", null, null, false, true);

    [Fact]
    public void RanksAccountsByRecentUseAndSuggestsActiveSuccessorForCompletedProject()
    {
        var purchases = new[]
        {
            Purchase(1, "2026-06-01", new FikenOrderLine(null, "Mat leir", 50000, 0, "6800", "NONE", 3)),
            Purchase(2, "2025-06-01", new FikenOrderLine(null, "Mat leir", 40000, 0, "6800", "NONE", 3)),
            Purchase(3, "2022-01-01", new FikenOrderLine(null, "Diverse", 1000, 0, "6540", "NONE", null))
        };
        var projects = new[]
        {
            new FikenProject(3, "3", "Landsleir 2025", null, "2025-01-01", "2025-12-31", true),
            new FikenProject(7, "7", "Landsleir 2026", null, "2026-01-01", null, false)
        };
        var budget = new BudgetYear { Year = 2026, Lines = [new BudgetLine { Name = "Landsleir", ProjectIds = [7] }] };

        var suggestion = PurchaseSuggestionBuilder.Build(
            purchases,
            [new FikenAccount("6800", "Kontorrekvisita"), new FikenAccount("6540", "Inventar")],
            projects,
            budget,
            "Mat til leiren",
            new DateOnly(2026, 11, 24));

        Assert.Equal(3, suggestion.PurchasesAnalysed);
        Assert.Equal("6800", suggestion.Accounts[0].Account);
        Assert.Equal("Kontorrekvisita", suggestion.Accounts[0].AccountName);
        Assert.Equal(2, suggestion.Accounts[0].TimesUsed);
        Assert.Equal(900m, suggestion.Accounts[0].TotalKr);
        Assert.True(suggestion.Accounts[0].Score > suggestion.Accounts[1].Score);

        var project = Assert.Single(suggestion.Projects);
        Assert.Equal(3, project.ProjectId);
        Assert.True(project.Completed);
        Assert.Equal(7, project.ActiveSuccessorProjectId);
        Assert.Equal("Landsleir 2026", project.ActiveSuccessorName);
    }

    [Fact]
    public void NoHistory_GivesNote()
    {
        var suggestion = PurchaseSuggestionBuilder.Build([], [], [], null, null, new DateOnly(2026, 1, 1));
        Assert.Empty(suggestion.Accounts);
        Assert.Contains(suggestion.Notes, n => n.Contains("Ingen tidligere kjøp"));
    }

    [Theory]
    [InlineData("Landsleir 2025", "landsleir")]
    [InlineData("Kretsting - 2026", "kretsting")]
    [InlineData("  Ledertrening   Høst 24 ", "ledertrening høst")]
    public void NormalizeProjectName_StripsYearsAndPunctuation(string name, string expected)
    {
        Assert.Equal(expected, PurchaseSuggestionBuilder.NormalizeProjectName(name));
    }
}
