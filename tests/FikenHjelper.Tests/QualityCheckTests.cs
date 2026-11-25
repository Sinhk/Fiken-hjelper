using System.Net;
using System.Text;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.QualityCheck;

namespace FikenHjelper.Tests;

public sealed class QualityCheckRulesTests
{
    private static readonly DateOnly Ref = new(2026, 12, 31);

    private static FikenSale Sale(
        long id,
        long? outstanding,
        string? date = "2026-10-01",
        string? due = "2026-10-15",
        string? number = null,
        bool settled = false,
        bool deleted = false,
        bool writeOff = false,
        IReadOnlyList<FikenProjectRef>? projects = null,
        string kind = "invoice",
        string? kid = null) =>
        new(id, number, date, due, kind, NetAmount: 10000, VatAmount: 0, settled, outstanding,
            Contact(1, "Testgruppe speidere"), deleted, writeOff, TotalPaid: 0, kid, projects);

    private static FikenContact Contact(long id, string name) =>
        new(id, name, Email: null, OrganizationNumber: null, CustomerNumber: null, CustomerAccountCode: null,
            Customer: true, Supplier: false, Inactive: false, DaysUntilInvoicingDueDate: null, Groups: null);

    private static FikenOrderLine Line(long? netPrice, long? netPriceInCurrency = null) =>
        new(LineId: null, Description: "Linje", NetPrice: netPrice, Vat: netPrice is null ? null : 0, Account: "6800",
            VatType: "OUTSIDE", ProjectId: null, NetPriceInCurrency: netPriceInCurrency);

    private static FikenInvoice Invoice(
        long id,
        string? issue,
        string? due,
        long gross,
        FikenSale? sale,
        IReadOnlyList<FikenProjectRef>? projects = null,
        string? kid = null) =>
        new(id, InvoiceNumber: 1000 + id, issue, due, gross, gross, "REF", kid,
            Contact(10, "Testgruppe speidere"), sale, projects);

    private static FikenPurchase Purchase(
        long id,
        string? date,
        string? due,
        long[] lineGross,
        long[]? payments = null,
        bool paid = false,
        string? identifier = "F-1",
        bool settled = false) =>
        new(id, TransactionId: null, identifier, date, due, "supplier", paid,
            lineGross.Select(g => Line(g)).ToList(),
            Contact(20, "Testleverandør AS"),
            Currency: "NOK",
            payments?.Select((a, i) => new FikenPayment(i, "2026-11-01", a, a)).ToList(),
            PurchaseAttachments: null,
            Deleted: false,
            settled,
            [new FikenProjectRef(5, "P5", "Kretsleir")]);

    [Theory]
    [InlineData("2026-12-31", null)]   // forfaller samme dag: ikke forfalt
    [InlineData("2027-01-10", null)]   // fram i tid
    [InlineData("2026-12-30", 1)]
    [InlineData("2026-11-30", 31)]
    public void DaysOverdue_CountsOnlyStrictlyAfterDueDate(string due, int? expected)
    {
        Assert.Equal(expected, QualityCheckRules.DaysOverdue(DateOnly.Parse(due), Ref));
    }

    [Fact]
    public void DaysOverdue_NullDueDate_IsNotOverdue()
    {
        Assert.Null(QualityCheckRules.DaysOverdue(null, Ref));
    }

    [Theory]
    [InlineData("2026-03-04", 2026, 3, 4)]
    [InlineData("2026-03-04T10:00:00Z", 2026, 3, 4)]
    public void ParseDate_AcceptsDateAndTimestamp(string value, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), QualityCheckRules.ParseDate(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("04.03.2026")]
    public void ParseDate_InvalidReturnsNull(string? value)
    {
        Assert.Null(QualityCheckRules.ParseDate(value));
    }

    [Fact]
    public void CustomerInvoices_UsesSaleOutstandingForPartlyPaidAndMarksOverdue()
    {
        var invoices = new[]
        {
            Invoice(1, "2026-10-01", "2026-10-15", 45000, Sale(101, outstanding: 20000)),
            Invoice(2, "2026-12-20", "2027-01-03", 30000, Sale(102, outstanding: 30000, due: "2027-01-03"))
        };

        var section = QualityCheckRules.BuildCustomerInvoiceSection(invoices, Ref);

        Assert.Equal(2, section.Findings.Count);
        var overdue = section.Findings[0];
        Assert.Equal("Faktura 1001", overdue.Reference);
        Assert.Equal(QualityStatus.Overdue, overdue.Status);
        Assert.Equal(77, overdue.DaysOverdue);
        Assert.Equal(20000, overdue.OutstandingOre);

        var open = section.Findings[1];
        Assert.Equal(QualityStatus.Open, open.Status);
        Assert.Null(open.DaysOverdue);

        Assert.Equal(50000, section.TotalOre);
        Assert.Equal(20000, section.OverdueOre);
        Assert.Equal(1, section.OverdueCount);
        Assert.Equal(QualityStatus.Overdue, section.Status);
    }

    [Fact]
    public void CustomerInvoices_SkipsIssuedAfterReferenceDateAndSettledOrZero()
    {
        var invoices = new[]
        {
            Invoice(1, "2027-01-05", "2027-01-19", 45000, Sale(101, 45000)),
            Invoice(2, "2026-10-01", "2026-10-15", 45000, Sale(102, 45000, settled: true)),
            Invoice(3, "2026-10-01", "2026-10-15", 45000, Sale(103, 0)),
            Invoice(4, "2026-10-01", "2026-10-15", 45000, Sale(104, 45000, writeOff: true))
        };

        var section = QualityCheckRules.BuildCustomerInvoiceSection(invoices, Ref);

        Assert.Empty(section.Findings);
        Assert.Equal(QualityStatus.Ok, section.Status);
    }

    [Fact]
    public void CustomerInvoices_FallsBackToGrossWhenSaleMissing()
    {
        var section = QualityCheckRules.BuildCustomerInvoiceSection(
            [Invoice(1, "2026-10-01", "2026-10-15", 12345, sale: null, [new FikenProjectRef(7, "7", "Kurs")])],
            Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Equal(12345, finding.OutstandingOre);
        Assert.Equal("Kurs", finding.Project);
        Assert.Equal("Testgruppe speidere", finding.Counterparty);
    }

    [Fact]
    public void OtherSales_ExcludesSalesAlreadyCoveredByInvoices()
    {
        var invoiceSale = Sale(101, 45000);
        var invoices = new[] { Invoice(1, "2026-10-01", "2026-10-15", 45000, invoiceSale) };
        var sales = new[]
        {
            invoiceSale,
            Sale(201, 8000, number: "S-7", kind: "cash_sale"),
            Sale(202, 8000, deleted: true)
        };

        var section = QualityCheckRules.BuildOtherSalesSection(sales, invoices, Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Equal("Salg S-7", finding.Reference);
        Assert.Equal(8000, finding.OutstandingOre);
    }

    [Fact]
    public void OtherSales_DoesNotMixCurrenciesWhenOutstandingMissing()
    {
        var sale = Sale(301, outstanding: null, kind: "external_invoice") with { NetAmount = 10000, VatAmount = 0, TotalPaid = 2500 };

        var section = QualityCheckRules.BuildOtherSalesSection([sale], [], Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Null(finding.OutstandingOre);
        Assert.Equal(QualityStatus.CheckManually, finding.Status);
        Assert.Equal("Salg (id 301)", finding.Reference);
        Assert.Equal(1, section.UnknownAmountCount);
    }

    [Fact]
    public void SupplierPurchases_SumsLinesMinusPayments()
    {
        var purchases = new[]
        {
            Purchase(1, "2026-11-01", "2026-11-15", [10000, 5000], payments: [4000]),
            Purchase(2, "2026-11-01", "2026-11-15", [10000], payments: [10000]),
            Purchase(3, "2026-11-01", "2026-11-15", [10000], paid: true),
            Purchase(4, "2027-01-02", "2027-01-16", [10000]),
            Purchase(5, "2026-11-01", "2026-11-15", [10000], settled: true)
        };

        var section = QualityCheckRules.BuildSupplierPurchaseSection(purchases, Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Equal(11000, finding.OutstandingOre);
        Assert.Equal("Kjøp F-1", finding.Reference);
        Assert.Equal("Testleverandør AS", finding.Counterparty);
        Assert.Equal("Kretsleir", finding.Project);
        Assert.Equal(46, finding.DaysOverdue);
    }

    [Fact]
    public void Sections_SortOverdueFirstByOldestDueDate()
    {
        var purchases = new[]
        {
            Purchase(1, "2026-11-01", "2027-01-15", [100], identifier: "open"),
            Purchase(2, "2026-11-01", "2026-12-01", [100], identifier: "newer-overdue"),
            Purchase(3, "2026-11-01", "2026-11-01", [100], identifier: "oldest-overdue")
        };

        var section = QualityCheckRules.BuildSupplierPurchaseSection(purchases, Ref);

        Assert.Equal(
            ["Kjøp oldest-overdue", "Kjøp newer-overdue", "Kjøp open"],
            section.Findings.Select(f => f.Reference));
    }

    [Fact]
    public void CustomerInvoices_MatchesSaleByKidWhenEmbeddedSaleMissing()
    {
        var invoices = new[] { Invoice(1, "2026-10-01", "2026-10-15", 45000, sale: null, kid: "0012345") };
        var sales = new[] { Sale(101, outstanding: 15000, kid: "0012345") };

        var invoiceSection = QualityCheckRules.BuildCustomerInvoiceSection(invoices, Ref, sales);
        var otherSection = QualityCheckRules.BuildOtherSalesSection(sales, invoices, Ref);

        var finding = Assert.Single(invoiceSection.Findings);
        Assert.Equal(15000, finding.OutstandingOre);
        Assert.Null(finding.Note);
        Assert.Empty(otherSection.Findings);
    }

    [Fact]
    public void CustomerInvoices_SkipsDeletedSale()
    {
        var section = QualityCheckRules.BuildCustomerInvoiceSection(
            [Invoice(1, "2026-10-01", "2026-10-15", 45000, Sale(101, 45000, deleted: true))],
            Ref);

        Assert.Empty(section.Findings);
    }

    [Fact]
    public void CustomerInvoices_WithoutSaleNotesGrossFallback()
    {
        var section = QualityCheckRules.BuildCustomerInvoiceSection(
            [Invoice(1, "2026-10-01", "2026-10-15", 45000, sale: null)],
            Ref);

        Assert.Contains("delbetalinger", Assert.Single(section.Findings).Note);
    }

    [Fact]
    public void OtherSales_UnmatchedInvoiceSaleIsFlaggedForManualCheck()
    {
        var section = QualityCheckRules.BuildOtherSalesSection([Sale(101, 45000, kid: "999")], [], Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Equal(QualityStatus.CheckManually, finding.Status);
        Assert.Equal(45000, finding.OutstandingOre);
        Assert.Contains("to ganger", finding.Note);
    }

    [Fact]
    public void NegativeOutstanding_IsShownAsCreditAndReducesTotal()
    {
        var sales = new[]
        {
            Sale(201, -5000, kind: "cash_sale", number: "S-1"),
            Sale(202, 20000, kind: "cash_sale", number: "S-2", due: "2027-01-10")
        };

        var section = QualityCheckRules.BuildOtherSalesSection(sales, [], Ref);

        var credit = section.Findings.Single(f => f.Reference == "Salg S-1");
        Assert.Equal(QualityStatus.Credit, credit.Status);
        Assert.Null(credit.DaysOverdue);
        Assert.NotNull(credit.Note);
        Assert.Equal(15000, section.TotalOre);
    }

    [Fact]
    public void SupplierPurchases_ForeignCurrencyGivesUnknownAmount()
    {
        var purchase = Purchase(1, "2026-11-01", "2026-11-15", [0]) with
        {
            Lines = [Line(netPrice: null, netPriceInCurrency: 10000)]
        };
        var withForeignPayment = Purchase(2, "2026-11-01", "2026-11-15", [10000]) with
        {
            Payments = [new FikenPayment(1, "2026-11-05", 500, AmountInNok: null, Currency: "EUR")]
        };
        var withNokPaymentWithoutAmountInNok = Purchase(3, "2026-11-01", "2026-11-15", [10000]) with
        {
            Payments = [new FikenPayment(1, "2026-11-05", 4000, AmountInNok: null, Currency: null)]
        };

        Assert.Null(QualityCheckRules.PurchaseOutstandingInNok(purchase));
        Assert.Null(QualityCheckRules.PurchaseOutstandingInNok(withForeignPayment));
        Assert.Equal(6000, QualityCheckRules.PurchaseOutstandingInNok(withNokPaymentWithoutAmountInNok));

        var section = QualityCheckRules.BuildSupplierPurchaseSection([purchase], Ref);
        var finding = Assert.Single(section.Findings);
        Assert.Equal(QualityStatus.CheckManually, finding.Status);
        Assert.Equal(0, section.TotalOre);
        Assert.Equal(1, section.UnknownAmountCount);
    }

    [Fact]
    public void FindingWithoutDate_IsIncluded()
    {
        var section = QualityCheckRules.BuildSupplierPurchaseSection(
            [Purchase(1, date: null, due: null, [10000])],
            Ref);

        var finding = Assert.Single(section.Findings);
        Assert.Equal(QualityStatus.Open, finding.Status);
        Assert.Null(finding.Date);
    }

    [Fact]
    public void EmptyInput_GivesOkReport()
    {
        var report = QualityCheckService.Build([], [], [], Ref, DateTimeOffset.UnixEpoch);

        Assert.Equal(3, report.Sections.Count);
        Assert.All(report.Sections, s => Assert.Equal(QualityStatus.Ok, s.Status));
        Assert.Equal(QualityStatus.Ok, report.Status);
    }
}

public sealed class QualityCheckTextFormatterTests
{
    [Theory]
    [InlineData(123450, "1\u00A0234,50")]
    [InlineData(5, "0,05")]
    [InlineData(0, "0,00")]
    [InlineData(100000000, "1\u00A0000\u00A0000,00")]
    [InlineData(-25050, "-250,50")]
    public void FormatKroner_UsesNorwegianFormat(long ore, string expected)
    {
        Assert.Equal(expected, QualityCheckTextFormatter.FormatKroner(ore));
    }

    [Theory]
    [InlineData(123450, "1234,50")]
    [InlineData(-25050, "-250,50")]
    [InlineData(100000000, "1000000,00")]
    public void FormatKronerPlain_HasNoGroupSeparator(long ore, string expected)
    {
        Assert.Equal(expected, QualityCheckTextFormatter.FormatKronerPlain(ore));
    }

    private static QualityCheckReport SampleReport()
    {
        var findings = new[]
        {
            new QualityFinding("Faktura 1001", "Gruppe\tA | B", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15),
                77, 123450, "Kretsleir", QualityStatus.Overdue),
            new QualityFinding("Faktura 1002", "Gruppe C", new DateOnly(2026, 12, 20), new DateOnly(2027, 1, 3),
                null, 30000, null, QualityStatus.Open),
            new QualityFinding("Faktura 1003", "Gruppe D", new DateOnly(2026, 12, 1), null,
                null, null, null, QualityStatus.CheckManually, Note: "Sjekk i Fiken")
        };
        return new QualityCheckReport(
            new DateOnly(2026, 12, 31),
            DateTimeOffset.UnixEpoch,
            [
                new QualityCheckSection(QualitySectionKeys.CustomerInvoices, "Ubetalte kundefakturaer", findings),
                new QualityCheckSection(QualitySectionKeys.SupplierPurchases, "Ubetalte kjøp", [])
            ]);
    }

    [Fact]
    public void ToTabSeparated_HasHeadingsRowsAndSums()
    {
        var text = QualityCheckTextFormatter.ToTabSeparated(SampleReport());
        var lines = text.Split('\n');

        Assert.Equal("Kvalitetssjekk per 31.12.2026", lines[0]);
        Assert.Contains(
            "Ubetalte kundefakturaer: 3 stk, 1\u00A0534,50 kr (forfalt: 1 stk, 1\u00A0234,50 kr); 1 stk med ukjent beløp er ikke med i summen",
            lines);
        Assert.Contains(
            "Forfalt\tFaktura 1001\tGruppe A | B\t01.10.2026\t15.10.2026\t77\t1234,50\tKretsleir\t",
            lines);
        Assert.Contains("Åpen\tFaktura 1002\tGruppe C\t20.12.2026\t03.01.2027\t\t300,00\t\t", lines);
        Assert.Contains("Sjekk\tFaktura 1003\tGruppe D\t01.12.2026\t\t\tukjent\t\tSjekk i Fiken", lines);
        Assert.Contains("Sum\t\t\t\t\t\t1534,50\t\t", lines);
        Assert.Contains("Ubetalte kjøp: ingen", lines);
        Assert.All(
            lines.Where(l => l.StartsWith("Forfalt") || l.StartsWith("Åpen") || l.StartsWith("Sjekk\t") || l.StartsWith("Sum")),
            l => Assert.Equal(8, l.Count(c => c == '\t')));
    }

    [Fact]
    public void ToMarkdown_EscapesPipesAndBuildsTable()
    {
        var md = QualityCheckTextFormatter.ToMarkdown(SampleReport());

        Assert.Contains("## Kvalitetssjekk per 31.12.2026", md);
        Assert.Contains("| Status | Referanse | Motpart | Dato | Forfall | Dager forfalt | Utestående (kr) | Prosjekt | Merknad |", md);
        Assert.Contains("|---|---|---|---|---|---:|---:|---|---|", md);
        Assert.Contains("| **Sum** |  |  |  |  |  | **1\u00A0534,50** |  |  |", md);
        Assert.Contains("| Forfalt | Faktura 1001 | Gruppe A \\| B |", md);
        Assert.Contains("**1\u00A0534,50**", md);
        Assert.Contains("Ingen åpne poster.", md);
    }
}

public sealed class QualityCheckFikenClientTests
{
    private static FikenClient CreateClient(StubHttpHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri(FikenClient.DefaultBaseAddress) };
        var store = new LocalConfigStore(
            new AppSettingsDefaults(FikenApiToken: "test-token-123"),
            Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N")));
        return new FikenClient(http, store);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ReadMethods_UseGetWithExpectedFilters()
    {
        var seen = new List<(HttpMethod Method, string Url)>();
        var handler = new StubHttpHandler((req, _) =>
        {
            seen.Add((req.Method, req.RequestUri!.PathAndQuery));
            return Task.FromResult(Json("[]"));
        });
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var ct = TestContext.Current.CancellationToken;
            await client.GetUnsettledInvoicesAsync("krets", ct);
            await client.GetSalesAsync("krets", settled: false, ct: ct);
            await client.GetPurchasesAsync("krets", paid: false, ct: ct);
        }

        Assert.All(seen, s => Assert.Equal(HttpMethod.Get, s.Method));
        Assert.Equal(
        [
            "/api/v2/companies/krets/invoices?settled=false&page=0&pageSize=100",
            "/api/v2/companies/krets/sales?settled=false&page=0&pageSize=100",
            "/api/v2/companies/krets/purchases?sortBy=date%20desc&paid=false&page=0&pageSize=100"
        ], seen.Select(s => s.Url));
    }

    [Fact]
    public async Task Deserializes_ProjectAsObjectArrayOrNull()
    {
        const string invoicesJson = """
            [
              {"invoiceId":1,"invoiceNumber":10001,"issueDate":"2026-10-01","dueDate":"2026-10-15",
               "gross":45000,"grossInNok":45000,"customer":{"contactId":5,"name":"Gruppe A"},
               "sale":{"saleId":9,"outstandingBalance":45000,"settled":false,"project":{"projectId":3,"name":"Leir"}},
               "project":{"projectId":3,"number":"3","name":"Leir"}},
              {"invoiceId":2,"invoiceNumber":10002,"gross":100,"project":null}
            ]
            """;
        const string purchasesJson = """
            [
              {"purchaseId":7,"identifier":"F-1","date":"2026-11-01","dueDate":"2026-11-15","paid":false,
               "lines":[{"description":"Mat","netPrice":10000,"vat":0,"account":"6800"}],
               "supplier":{"contactId":8,"name":"Butikk"},
               "payments":[{"paymentId":1,"date":"2026-11-05","amount":2500,"amountInNok":2500}],
               "project":[{"projectId":3,"name":"Leir"},{"projectId":4,"name":"Kurs"}]}
            ]
            """;

        var handler = new StubHttpHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/invoices") ? invoicesJson
                : path.EndsWith("/purchases") ? purchasesJson
                : "[]";
            return Task.FromResult(Json(body));
        });
        var client = CreateClient(handler, out var http);
        using (http)
        {
            var service = new QualityCheckService(client, client, TimeProvider.System);
            var report = await service.RunAsync("krets", new DateOnly(2026, 12, 31), TestContext.Current.CancellationToken);

            var invoices = report.Sections.Single(s => s.Key == QualitySectionKeys.CustomerInvoices);
            Assert.Equal(2, invoices.Findings.Count);
            var first = invoices.Findings.Single(f => f.Reference == "Faktura 10001");
            Assert.Equal("Leir", first.Project);
            Assert.Equal("Gruppe A", first.Counterparty);
            Assert.Equal(QualityStatus.Overdue, first.Status);
            var second = invoices.Findings.Single(f => f.Reference == "Faktura 10002");
            Assert.Null(second.Project);
            Assert.Equal("(ukjent)", second.Counterparty);

            var purchase = Assert.Single(report.Sections.Single(s => s.Key == QualitySectionKeys.SupplierPurchases).Findings);
            Assert.Equal(7500, purchase.OutstandingOre);
            Assert.Equal("Leir, Kurs", purchase.Project);
        }
    }
}
