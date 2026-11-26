using ClosedXML.Excel;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Grants;

namespace FikenHjelper.Tests;

internal static class GrantOutputData
{
    public static readonly DateOnly Today = new(2027, 6, 15);

    public static readonly FikenAccount[] Accounts = [new("6800", "Kontorrekvisita"), new("7140", "Reisekostnad")];

    public static readonly FikenProject[] Projects = [new(GrantTestData.Kurs, "900", "Lederkurs 2027", null, null, null, false)];

    public static Grant KursGrant()
    {
        var grant = GrantTestData.Project("Lederkurs 2027", GrantTestData.Kurs);
        grant.Grantor = "Eksempel kommune";
        grant.Scheme = "Kurs & opplæring";
        grant.GrantedAmount = 50000m;
        grant.AppliedAmount = 60000m;
        grant.UseFrom = new DateOnly(2027, 1, 1);
        grant.UseTo = new DateOnly(2027, 12, 31);
        grant.Payments = [new GrantPayment { Amount = 10000m, Date = new DateOnly(2027, 2, 1), Note = "Forskudd" }];
        return grant;
    }

    public static FikenJournalEntry[] Entries =>
    [
        GrantTestData.Cost(1, "6800", 1234.50m, GrantTestData.Kurs, "2027-03-01"),
        GrantTestData.Cost(2, "7140", 300m, GrantTestData.Kurs, "2027-04-01"),
        GrantTestData.Income(3, "3200", 900m, GrantTestData.Kurs, "Deltakeravgift", "2027-05-01")
    ];

    public static GrantReport Report(Grant grant, IEnumerable<FikenJournalEntry>? entries) =>
        GrantReportBuilder.Build(grant, [grant], entries, Accounts, Projects);
}

public sealed class GrantTextFormatterTests
{
    private static GrantReport Full() => GrantOutputData.Report(GrantOutputData.KursGrant(), GrantOutputData.Entries);

    [Fact]
    public void TabSeparated_UsesPlainNumbersAndTabs()
    {
        var text = GrantTextFormatter.ToTabSeparated(Full());

        Assert.Contains("Tilskudd: Lederkurs 2027", text);
        Assert.Contains("Tilskuddsgiver\tEksempel kommune", text);
        Assert.Contains("Bruksperiode\t01.01.2027 – 31.12.2027", text);
        Assert.Contains("Innvilget (kr)\t50000,00", text);
        Assert.Contains("Mottatt (kr)\t10000,00", text);
        Assert.Contains("Brukt (kr)\t1534,50", text);
        Assert.Contains("Gjenstår å motta (kr)\t40000,00", text);
        Assert.Contains("Ubrukt (mottatt − brukt) (kr)\t8465,50", text);
        Assert.Contains("6800\tKontorrekvisita\t1234,50", text);
        Assert.Contains("Sum\t\t1534,50", text);
        Assert.Contains("Andre inntekter på prosjektet", text);
        Assert.Contains("3200\t\t900,00", text);
        Assert.DoesNotContain(' ', text);
    }

    [Fact]
    public void Markdown_UsesGroupedNumbers()
    {
        var markdown = GrantTextFormatter.ToMarkdown(Full());

        Assert.Contains("## Tilskudd: Lederkurs 2027", markdown);
        Assert.Contains("| Konto | Navn | Beløp (kr) |", markdown);
        Assert.Contains("|---|---|---:|", markdown);
        Assert.Contains("| 6800 | Kontorrekvisita | 1 234,50 |", markdown);
        Assert.Contains("| **Sum** |  | **1 534,50** |", markdown);
        Assert.Contains("| Innvilget (kr) | 50 000,00 |", markdown);
    }

    [Fact]
    public void Html_IsARealTableAndEscapesUserText()
    {
        var grant = GrantOutputData.KursGrant();
        grant.Name = "Kurs <script>alert(1)</script>";
        grant.Grantor = "A & B <b>";
        var raw = GrantTextFormatter.ToHtml(GrantOutputData.Report(grant, GrantOutputData.Entries));
        // Tegn utenfor ASCII (æøå, hardt mellomrom) skrives som tegnreferanser, så sammenlign etter avkoding.
        var html = System.Net.WebUtility.HtmlDecode(raw);

        Assert.Contains("<table", html);
        Assert.Contains("<th", html);
        Assert.DoesNotContain("<script>", raw);
        Assert.Contains("&lt;script&gt;", raw);
        Assert.Contains("A &amp; B &lt;b&gt;", raw);
        Assert.Contains("1 234,50", html);
        Assert.Contains("<b>1 534,50</b>", html);
    }

    [Fact]
    public void OtherIncome_CanBeLeftOut()
    {
        var text = GrantTextFormatter.ToTabSeparated(Full(), includeOtherIncome: false);

        Assert.DoesNotContain("Andre inntekter", text);
        Assert.DoesNotContain("3200", text);
    }

    [Fact]
    public void MissingFikenData_IsWrittenAsTextNotZero()
    {
        var grant = GrantOutputData.KursGrant();
        grant.Payments = [new GrantPayment { JournalEntryId = 77, Date = new DateOnly(2027, 2, 1) }];
        var report = GrantOutputData.Report(grant, null);

        var text = GrantTextFormatter.ToTabSeparated(report);

        Assert.Contains("Mottatt (kr)\tmangler Fiken-data", text);
        Assert.Contains("Brukt (kr)\tmangler Fiken-data", text);
        Assert.Contains("Mangler Fiken-data, så bruken kan ikke vises.", text);
        Assert.Contains("Gjenstår å motta (kr)\tukjent", text);
        Assert.DoesNotContain("Brukt (kr)\t0,00", text);
        Assert.DoesNotContain("Mottatt (kr)\t0,00", text);
        Assert.DoesNotContain("Gjenstår å motta (kr)\t0,00", text);
    }

    [Fact]
    public void UnregisteredUse_IsWrittenAsNotRegistered()
    {
        var grant = new Grant { Name = "Drift 2027", Grantor = "Eksempel fylke", Kind = GrantKind.Operating, Status = GrantStatus.Granted };
        var text = GrantTextFormatter.ToTabSeparated(GrantOutputData.Report(grant, []));

        Assert.Contains("Brukt (kr)\tikke registrert", text);
        Assert.Contains("Bruk er ikke registrert.", text);
        Assert.Contains("Innvilget (kr)\tikke satt", text);
    }

    [Fact]
    public void Overview_ListsGrantsAndMarksPartialTotals()
    {
        var first = GrantOutputData.KursGrant();
        var second = new Grant { Name = "Leir 2027", Grantor = "Eksempel stiftelse", Status = GrantStatus.Submitted, AppliedAmount = 20000m };
        var reports = GrantReportBuilder.BuildAll([first, second], GrantOutputData.Entries, GrantOutputData.Accounts, GrantOutputData.Projects);

        var tabs = GrantTextFormatter.OverviewToTabSeparated(reports, "år 2027");
        var markdown = GrantTextFormatter.OverviewToMarkdown(reports);

        Assert.Contains("Utvalg: år 2027", tabs);
        Assert.Contains("Navn\tTilskuddsgiver\tType\tStatus", tabs);
        Assert.Contains("Lederkurs 2027\tEksempel kommune\tProsjekttilskudd\tInnvilget\t60000,00\t50000,00\t10000,00\t1534,50\t40000,00", tabs);
        Assert.Contains("Leir 2027\tEksempel stiftelse\tProsjekttilskudd\tSendt\t20000,00\t–\t0,00\tikke registrert\t–", tabs);
        Assert.Contains("Sum (2 tilskudd)\t\t\t\t80000,00\t50000,00 *\t10000,00\t1534,50 *\t40000,00 *", tabs);
        Assert.Contains("* Delsum, siden noen tilskudd mangler verdi: innvilget for 1 av 2 tilskudd; brukt for 1 av 2 tilskudd; gjenstår for 1 av 2 tilskudd.", tabs);
        // Stjernen må ikke bli kursiv eller punktliste i Markdown.
        Assert.Contains("50 000,00 \\*", markdown);
        Assert.Contains("\\* Delsum", markdown);
    }

    [Fact]
    public void Overview_WithoutFikenData_ShowsTextForMissingValues()
    {
        var grant = GrantOutputData.KursGrant();
        var reports = GrantReportBuilder.BuildAll([grant], null, null, null);

        var tabs = GrantTextFormatter.OverviewToTabSeparated(reports);

        Assert.Contains("mangler Fiken-data", tabs);
        Assert.Contains("Sum (1 tilskudd)", tabs);
    }

    [Fact]
    public void Overview_Html_EscapesAndHasTable()
    {
        var grant = GrantOutputData.KursGrant();
        grant.Name = "A<B";
        var html = GrantTextFormatter.OverviewToHtml(GrantReportBuilder.BuildAll([grant], GrantOutputData.Entries), "år 2027 & status");

        Assert.Contains("<table", html);
        Assert.Contains("A&lt;B", html);
        Assert.Contains("2027 &amp; status", html);
    }

    [Fact]
    public void Overview_Empty_SaysSo()
    {
        Assert.Contains("Ingen tilskudd passer utvalget.", GrantTextFormatter.OverviewToTabSeparated([]));
    }
}

public sealed class GrantWorkbookTests
{
    private static XLWorkbook Open(byte[] bytes) => new(new MemoryStream(bytes));

    private static IXLRow FindRow(IXLWorksheet ws, string firstCell) =>
        ws.RowsUsed().First(r => r.Cell(1).GetString() == firstCell);

    [Fact]
    public void Grant_HasThreeSheetsWithNumbersAndTotals()
    {
        var report = GrantOutputData.Report(GrantOutputData.KursGrant(), GrantOutputData.Entries);
        using var wb = Open(GrantWorkbookExporter.ExportGrant(report, GrantOutputData.Projects, GrantOutputData.Today));

        Assert.Equal(["Sammendrag", "Bruk per konto", "Bilag"], wb.Worksheets.Select(w => w.Name));

        var summary = wb.Worksheet("Sammendrag");
        Assert.Equal("Tilskudd: Lederkurs 2027", summary.Cell(1, 1).GetString());
        Assert.Equal("Eksempel kommune", FindRow(summary, "Tilskuddsgiver").Cell(2).GetString());
        Assert.Equal(new DateTime(2027, 1, 1), FindRow(summary, "Bruksperiode fra").Cell(2).GetDateTime());
        Assert.Equal(50000m, FindRow(summary, "Innvilget (kr)").Cell(2).GetDouble() is var g ? (decimal)g : 0m);
        Assert.Equal(1534.50, FindRow(summary, "Brukt (kr)").Cell(2).GetDouble());
        Assert.Equal(40000, FindRow(summary, "Gjenstår å motta (kr)").Cell(2).GetDouble());
        Assert.Equal(8465.50, FindRow(summary, "Ubrukt (mottatt − brukt) (kr)").Cell(2).GetDouble());
        Assert.Contains("#,##0.00", FindRow(summary, "Brukt (kr)").Cell(2).Style.NumberFormat.Format);
        var manualPayment = summary.RowsUsed().First(r => r.Cell(1).GetString() == "Manuell");
        Assert.Equal(10000, manualPayment.Cell(2).GetDouble());
        Assert.Equal(new DateTime(2027, 2, 1), manualPayment.Cell(3).GetDateTime());

        var byAccount = wb.Worksheet("Bruk per konto");
        Assert.Equal(["Konto", "Navn", "Sum (kr)"], new[] { 1, 2, 3 }.Select(c => byAccount.Cell(2, c).GetString()));
        Assert.Equal(6800, byAccount.Cell(3, 1).GetDouble());
        Assert.Equal("Kontorrekvisita", byAccount.Cell(3, 2).GetString());
        Assert.Equal(1234.50, byAccount.Cell(3, 3).GetDouble());
        Assert.Equal(300, byAccount.Cell(4, 3).GetDouble());
        var sumRow = byAccount.RowsUsed().First(r => r.Cell(1).GetString() == "Sum");
        Assert.Equal(1534.50, sumRow.Cell(3).GetDouble());
        // Andre inntekter står for seg med egen sum.
        var incomeSum = byAccount.RowsUsed().Last(r => r.Cell(1).GetString() == "Sum");
        Assert.Equal(900, incomeSum.Cell(3).GetDouble());

        var postings = wb.Worksheet("Bilag");
        Assert.Equal("Dato", postings.Cell(1, 1).GetString());
        Assert.Equal("Beløp for tilskuddet (kr)", postings.Cell(1, 8).GetString());
        Assert.Equal("Prosjektets fulle beløp (kr)", postings.Cell(1, 9).GetString());
        Assert.Equal(new DateTime(2027, 3, 1), postings.Cell(2, 1).GetDateTime());
        Assert.Equal(1, postings.Cell(2, 2).GetDouble());
        Assert.Equal("Lederkurs 2027", postings.Cell(2, 6).GetString());
        Assert.Equal(100, postings.Cell(2, 7).GetDouble());
        Assert.Equal(1234.50, postings.Cell(2, 8).GetDouble());
        Assert.Equal("Bruk", postings.Cell(2, 10).GetString());
        var income = postings.RowsUsed().Single(r => r.Cell(10).GetString() == "Annen inntekt");
        Assert.Equal(900, income.Cell(9).GetDouble());
        Assert.True(income.Cell(8).IsEmpty());
    }

    [Fact]
    public void Grant_WithoutFikenData_LeavesCellsEmptyWithExplanation()
    {
        var grant = GrantOutputData.KursGrant();
        grant.Payments = [new GrantPayment { JournalEntryId = 77, Date = new DateOnly(2027, 2, 1) }];
        var report = GrantOutputData.Report(grant, null);
        using var wb = Open(GrantWorkbookExporter.ExportGrant(report, null, GrantOutputData.Today));

        var summary = wb.Worksheet("Sammendrag");
        var used = FindRow(summary, "Brukt (kr)");
        Assert.True(used.Cell(2).IsEmpty());
        Assert.Equal("Mangler Fiken-data.", used.Cell(3).GetString());
        var received = FindRow(summary, "Mottatt (kr)");
        Assert.True(received.Cell(2).IsEmpty());
        Assert.NotEqual(string.Empty, received.Cell(3).GetString());
        Assert.True(FindRow(summary, "Gjenstår å motta (kr)").Cell(2).IsEmpty());

        Assert.Contains("Mangler Fiken-data", wb.Worksheet("Bruk per konto").Cell(3, 1).GetString());
        Assert.Contains("mangler Fiken-data", wb.Worksheet("Bilag").Cell(2, 1).GetString());
    }

    [Fact]
    public void Grant_ManualUse_IsListedAmongPostings()
    {
        var grant = new Grant { Name = "Drift 2027", Grantor = "Eksempel fylke", Kind = GrantKind.Operating, Status = GrantStatus.Granted, GrantedAmount = 1000m };
        grant.ManualUses = [new GrantManualUse { Date = new DateOnly(2027, 5, 5), Amount = 250m, Description = "Husleie" }];
        using var wb = Open(GrantWorkbookExporter.ExportGrant(GrantOutputData.Report(grant, []), null, GrantOutputData.Today));

        var postings = wb.Worksheet("Bilag");
        Assert.Equal("Husleie", postings.Cell(2, 3).GetString());
        Assert.Equal(250, postings.Cell(2, 8).GetDouble());
        Assert.Equal("Manuell bruk", postings.Cell(2, 10).GetString());
        Assert.Equal(250, FindRow(wb.Worksheet("Sammendrag"), "Brukt (kr)").Cell(2).GetDouble());
    }

    [Fact]
    public void Overview_HasRowsTotalsAndPartialNote()
    {
        var first = GrantOutputData.KursGrant();
        var second = new Grant { Name = "Leir 2027", Grantor = "Eksempel stiftelse", Status = GrantStatus.Submitted, AppliedAmount = 20000m };
        var reports = GrantReportBuilder.BuildAll([first, second], GrantOutputData.Entries, GrantOutputData.Accounts, GrantOutputData.Projects);
        using var wb = Open(GrantWorkbookExporter.ExportOverview(reports, GrantOutputData.Today, "år 2027"));

        Assert.Equal(["Tilskudd", "Merknader"], wb.Worksheets.Select(w => w.Name));
        var ws = wb.Worksheet("Tilskudd");
        Assert.Equal("Søkt (kr)", ws.Cell(1, 6).GetString());
        Assert.Equal("Lederkurs 2027", ws.Cell(2, 1).GetString());
        Assert.Equal(60000, ws.Cell(2, 6).GetDouble());
        Assert.Equal(50000, ws.Cell(2, 7).GetDouble());
        Assert.Equal(10000, ws.Cell(2, 8).GetDouble());
        Assert.Equal(1534.50, ws.Cell(2, 9).GetDouble());
        Assert.Equal(40000, ws.Cell(2, 10).GetDouble());

        // Manglende verdier er tomme celler med forklaring, ikke 0.
        Assert.True(ws.Cell(3, 7).IsEmpty());
        Assert.True(ws.Cell(3, 9).IsEmpty());
        Assert.Equal("Brukt: ikke registrert", ws.Cell(3, 14).GetString());

        var sum = ws.Row(4);
        Assert.Equal("Sum (2 tilskudd)", sum.Cell(1).GetString());
        Assert.Equal(80000, sum.Cell(6).GetDouble());
        Assert.Equal(50000, sum.Cell(7).GetDouble());
        Assert.Equal(10000, sum.Cell(8).GetDouble());
        Assert.Contains("innvilget for 1 av 2 tilskudd", sum.Cell(14).GetString());

        Assert.Contains("Utvalg: år 2027", wb.Worksheet("Merknader").Cell(2, 1).GetString());
    }

    [Fact]
    public void Overview_Empty_HasNoDataRowsButSum()
    {
        using var wb = Open(GrantWorkbookExporter.ExportOverview([], GrantOutputData.Today));

        var ws = wb.Worksheet("Tilskudd");
        Assert.Equal("Sum (0 tilskudd)", ws.Cell(2, 1).GetString());
        Assert.True(ws.Cell(2, 6).IsEmpty());
    }

    [Fact]
    public void FileNames_AreSafeAndFreeOfPersonalData()
    {
        var grant = new Grant { Name = "Leir/2027: \"Sør\" <test>\\..\\x" };
        var name = GrantWorkbookExporter.FileName(grant, GrantOutputData.Today);

        Assert.Equal("Tilskudd Leir 2027 Sør test x 2027-06-15.xlsx", name);
        Assert.Equal("Tilskudd uten navn 2027-06-15.xlsx", GrantWorkbookExporter.FileName(new Grant { Name = " / " }, GrantOutputData.Today));
        Assert.Equal("Tilskudd 2027 Innvilget 2027-06-15.xlsx", GrantWorkbookExporter.OverviewFileName(2027, GrantStatus.Granted, GrantOutputData.Today));
        Assert.Equal("Tilskudd alle år 2027-06-15.xlsx", GrantWorkbookExporter.OverviewFileName(null, null, GrantOutputData.Today));
        Assert.True(GrantWorkbookExporter.FileName(new Grant { Name = new string('a', 500) }, GrantOutputData.Today).Length < 120);
    }
}
