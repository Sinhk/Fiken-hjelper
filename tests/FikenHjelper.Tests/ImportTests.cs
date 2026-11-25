using System.Text;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Import;
using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Tests;

public sealed class CsvTableReaderTests
{
    [Fact]
    public void Parse_MinSpeidingIndividualHeader_MakesDuplicateHeadersUnique()
    {
        var table = CsvTableReader.Parse(ImportTestData.IndividualHeader);

        Assert.Equal(43, table.Headers.Count);
        Assert.Equal(",", table.DetectedDelimiter);
        Assert.Contains("E-post", table.Headers);
        Assert.Contains("E-post (2)", table.Headers);
        Assert.Contains("E-post2", table.Headers);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void Parse_SemicolonWithQuotedDelimitersAndNewlines()
    {
        var csv = "Navn;Gruppe;Kommentar\r\n\"Testesen, Ola\";Molde;\"Linje 1\nLinje 2; med semikolon\"\r\nKari Prøve;\"Aukra \"\"Nord\"\"\";\r\n\r\n";

        var table = CsvTableReader.Parse(csv);

        Assert.Equal(";", table.DetectedDelimiter);
        Assert.Equal(["Navn", "Gruppe", "Kommentar"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Testesen, Ola", table.Rows[0][0]);
        Assert.Equal("Linje 1\nLinje 2; med semikolon", table.Rows[0][2]);
        Assert.Equal("Aukra \"Nord\"", table.Rows[1][1]);
    }

    [Fact]
    public void Parse_TabDelimited()
    {
        var table = CsvTableReader.Parse("Navn\tBeløp\nOla Test\t450\n");
        Assert.Equal("tab", table.DetectedDelimiter);
        Assert.Equal("450", table.Rows[0][1]);
    }

    [Fact]
    public void Read_DetectsUtf8WithAndWithoutBomAndWindows1252()
    {
        const string csv = "Navn;Gruppe\nPål Ødegård;Ålesund\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var withBom = CsvTableReader.Read(new MemoryStream([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)]));
        var withoutBom = CsvTableReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        var ansi = CsvTableReader.Read(new MemoryStream(Encoding.GetEncoding(1252).GetBytes(csv)));

        Assert.Equal("UTF-8 (BOM)", withBom.DetectedEncoding);
        Assert.Equal("UTF-8", withoutBom.DetectedEncoding);
        Assert.Equal("Windows-1252", ansi.DetectedEncoding);
        foreach (var table in new[] { withBom, withoutBom, ansi })
        {
            Assert.Equal("Navn", table.Headers[0]);
            Assert.Equal("Pål Ødegård", table.Rows[0][0]);
            Assert.Equal("Ålesund", table.Rows[0][1]);
        }
    }

    [Fact]
    public void Parse_QuoteInsideUnquotedField_IsLiteral()
    {
        var table = CsvTableReader.Parse("Navn;Gruppe;Kommentar\nOla Test;Molde;Tar med 12\" panne\nKari Test;Aukra;Ingen\nPer Test;Molde;Ingen\n");

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Tar med 12\" panne", table.Rows[0][2]);
    }

    [Fact]
    public void Parse_UnclosedQuote_Throws()
    {
        Assert.Throws<InvalidDataException>(() => CsvTableReader.Parse("Navn;Gruppe\n\"Ola Test;Molde\nKari;Aukra\n"));
    }

    [Fact]
    public void Parse_StripsBomInString()
    {
        var table = CsvTableReader.Parse("\uFEFFNavn;Gruppe\nOla;Molde\n");
        Assert.Equal(0, table.IndexOf("Navn"));
    }

    [Theory]
    [InlineData("Navn;Gruppe;Allergi, diett, medisin, annet\nOla;Molde;Ingen\nKari;Aukra;Nøtter\n", ";")]
    [InlineData("\t\t\nNavn;Gruppe\nOla;Molde\n", ";")]
    [InlineData("Navn,Gruppe\n\"Test; Ola\",Molde\n", ",")]
    public void DetectDelimiter_UsesConsistentColumnCount(string csv, string expected)
    {
        Assert.Equal(expected, CsvTableReader.Parse(csv).DetectedDelimiter);
    }

    [Fact]
    public void DetectDelimiter_CommaFileWithQuotedSemicolons_PicksComma()
    {
        var table = CsvTableReader.Parse("\"Navn\",\"Allergi; diett\"\n\"Ola Test\",\"Nøtter; gluten\"\n");

        Assert.Equal(",", table.DetectedDelimiter);
        Assert.Equal(["Navn", "Allergi; diett"], table.Headers);
        Assert.Equal("Nøtter; gluten", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_EmptyFile_Throws()
    {
        Assert.Throws<InvalidDataException>(() => CsvTableReader.Parse("\r\n \r\n"));
    }
}

public sealed class ImportValueParserTests
{
    [Theory]
    [InlineData("450", 450)]
    [InlineData("450,00", 450)]
    [InlineData("450.00", 450)]
    [InlineData("1 250,50", 1250.50)]
    [InlineData("1.250,50", 1250.50)]
    [InlineData("1.250", 1250)]
    [InlineData("kr 450", 450)]
    [InlineData("450 kr", 450)]
    [InlineData("450,-", 450)]
    [InlineData("Speider (300 kr)", 300)]
    [InlineData("Gratis", 0)]
    [InlineData("0", 0)]
    [InlineData("1.250 kr", 1250)]
    [InlineData("1.250,50 kr", 1250.50)]
    [InlineData("1.250,-", 1250)]
    [InlineData("kr 1.250", 1250)]
    [InlineData("kr 1.250,50", 1250.50)]
    [InlineData("NOK 1,250.50", 1250.50)]
    [InlineData("-450 kr", -450)]
    [InlineData("-450", -450)]
    [InlineData("0.500", 0.5)]
    [InlineData("1\u00A0250 kr", 1250)]
    [InlineData("Speider (1\u00A0250 kr)", 1250)]
    [InlineData("Speider (1.250 kr)", 1250)]
    [InlineData("Lederkurs: 700.00", 700)]
    [InlineData("Sommerleir: 1 500.00", 1500)]
    [InlineData("Sommerleir: 1.500,00", 1500)]
    [InlineData("Sommerleir: 1,500.00", 1500)]
    [InlineData("Sommerleir: 1.500", 1500)]
    [InlineData("Sommerleir: 1500,5", 1500.5)]
    [InlineData("Avgift: 1.5", 1.5)]
    [InlineData("Avgift: 12.50", 12.5)]
    [InlineData("Avgift: 1,5", 1.5)]
    [InlineData("Avgift: 1 500", 1500)]
    [InlineData("Avgift: -700.00", -700)]
    public void ParseAmountNok_ParsesNorwegianFormats(string input, double expected)
    {
        Assert.Equal((decimal)expected, ImportValueParser.ParseAmountNok(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Speider")]
    [InlineData("ikke betalt")]
    [InlineData("Betalt 2026")]
    [InlineData("Del 1 av 2")]
    [InlineData("450 + 50")]
    [InlineData("Dagspass lørdag 10:00-18:30")]
    [InlineData("Oppmøte kl 18:00")]
    [InlineData("Avgift: 1 500,000")]
    [InlineData("Avgift: 1.500,000")]
    public void ParseAmountNok_ReturnsNullForNonAmounts(string input)
    {
        Assert.Null(ImportValueParser.ParseAmountNok(input));
    }

    [Theory]
    [InlineData("Ja", true)]
    [InlineData("x", true)]
    [InlineData("1", true)]
    [InlineData("Nei", false)]
    [InlineData("", false)]
    public void ParseBool(string input, bool expected) => Assert.Equal(expected, ImportValueParser.ParseBool(input));

    [Theory]
    [InlineData("Ja", true)]
    [InlineData("2026-11-15 06:15", true)]
    [InlineData("Nei", false)]
    [InlineData("-", false)]
    [InlineData("", false)]
    public void ParseFlag(string input, bool expected) => Assert.Equal(expected, ImportValueParser.ParseFlag(input));

    [Theory]
    [InlineData("12345", 12345L)]
    [InlineData("12345.0", 12345L)]
    [InlineData("12345678901234567890", null)]
    [InlineData("abc", null)]
    public void ParseLong_HandlesExcelFormatAndOverflow(string input, long? expected)
        => Assert.Equal(expected, ImportValueParser.ParseLong(input));

    [Fact]
    public void Slugify_HandlesNorwegianLetters()
    {
        Assert.Equal("kurshelg-hosten-2026-aere", ImportValueParser.Slugify("Kurshelg høsten 2026 – Ære!"));
        Assert.Equal("uten-navn", ImportValueParser.Slugify("  "));
    }
}

public sealed class ImportProfileSuggesterTests
{
    [Theory]
    [InlineData("Generelt - Hva heter patruljen din?")]
    [InlineData("Generelt - Er det noe annet vi bør vite?")]
    [InlineData("Generelt - Har du allergier?")]
    [InlineData("Påmelding - Hvilken farge liker du?")]
    public void Suggest_FormQuestionsAreNotIncluded(string header)
    {
        var profile = ImportProfileSuggester.Suggest(["Navn", header]);
        Assert.Empty(profile.ExtraColumns);
    }

    [Fact]
    public void Suggest_MinSpeidingIndividualExport()
    {
        var headers = CsvTableReader.Parse(ImportTestData.IndividualHeader).Headers;

        var profile = ImportProfileSuggester.Suggest(headers);

        Assert.Equal("Navn", profile.FullNameColumn);
        Assert.Null(profile.FirstNameColumn);
        Assert.Equal("Medlemsnummer", profile.MemberNoColumn);
        Assert.Equal("Gruppe", profile.GroupColumn);
        Assert.Equal("Deltakeravgift", profile.CategoryColumn);
        Assert.Equal("Totalbeløp", profile.AmountColumn);
        Assert.True(profile.AmountIsTotal);
        Assert.Equal("Avmeldingsdato", profile.CancelledDateColumn);
        Assert.Equal("Påmeldtdato", profile.RegistrationDateColumn);
        Assert.Null(profile.QuantityColumn);
        Assert.Null(profile.ConfirmedColumn);
        Assert.Equal(["E-post", "E-post (2)", "Generelt - E-postadresse"], profile.EmailColumns);

        // Svar på påmeldingsskjemaet og sensitive kolonner (helse, kontaktinfo) tas ikke med.
        Assert.Empty(profile.ExtraColumns);
        Assert.DoesNotContain("Foresatt 1 e-post", profile.EmailColumns);
    }

    [Fact]
    public void Suggest_MinSpeidingGroupReport()
    {
        var headers = CsvTableReader.Parse(ImportTestData.GroupReportHeader).Headers;

        var profile = ImportProfileSuggester.Suggest(headers);

        Assert.Equal("Navn", profile.FullNameColumn);
        Assert.Equal("Gruppenummer", profile.GroupIdColumn);
        Assert.Equal("Gruppe", profile.GroupColumn);
        Assert.Equal("Deltakeravgift", profile.CategoryColumn);
        Assert.Equal("Antall", profile.AmountColumn);
        Assert.False(profile.AmountIsTotal);
        Assert.Null(profile.QuantityColumn);
        Assert.Equal("Bekreftet", profile.ConfirmedColumn);
        Assert.Equal("Til stede", profile.AttendedColumn);
        Assert.Equal("Innsjekket", profile.CheckedInColumn);
        Assert.Equal("Avmeldingsdato", profile.CancelledDateColumn);
        Assert.Equal(["E-post"], profile.EmailColumns);
        Assert.Empty(profile.ExtraColumns);
    }

    [Fact]
    public void Suggest_FirstAndLastNameAndStatusColumn()
    {
        var profile = ImportProfileSuggester.Suggest(["Fornavn", "Etternavn", "Speidergruppe", "Avmeldt", "Pris", "Kommentar"]);

        Assert.Equal("Fornavn", profile.FirstNameColumn);
        Assert.Equal("Etternavn", profile.LastNameColumn);
        Assert.Null(profile.FullNameColumn);
        Assert.Equal("Speidergruppe", profile.GroupColumn);
        Assert.Equal("Avmeldt", profile.StatusColumn);
        Assert.Contains("ja", profile.CancelledValues);
        Assert.Equal("Pris", profile.AmountColumn);
        Assert.False(profile.AmountIsTotal);
        Assert.Equal(["Kommentar"], profile.ExtraColumns);
    }

    [Fact]
    public void Suggest_KeepsSingleFirstNameColumn()
    {
        var profile = ImportProfileSuggester.Suggest(["Fornavn", "Gruppe"]);
        Assert.Equal("Fornavn", profile.FirstNameColumn);
        Assert.True(ImportProfileSuggester.HasName(profile));
    }

    [Fact]
    public void ChooseProfile_AcceptsSavedProfileWhenOnlySomeEmailColumnsExist()
    {
        var individualHeaders = CsvTableReader.Parse(ImportTestData.IndividualHeader).Headers;
        var saved = ImportProfileSuggester.Suggest(individualHeaders);
        saved.Name = "Min speiding individuell";

        // Neste arrangement har ikke skjemaspørsmålet «Generelt - E-postadresse».
        var otherHeaders = individualHeaders.Where(h => h != "Generelt - E-postadresse").ToList();
        var (chosen, isSaved) = ImportProfileSuggester.ChooseProfile(otherHeaders, [saved]);

        Assert.True(isSaved);
        Assert.Equal("Min speiding individuell", chosen.Name);
    }

    [Fact]
    public void ChooseProfile_PrefersSavedProfileWhenAllColumnsExist()
    {
        var headers = CsvTableReader.Parse(ImportTestData.GroupReportHeader).Headers;
        var matching = new ImportProfile { Name = "Gruppe", FullNameColumn = "Navn", GroupColumn = "Gruppe", CategoryColumn = "Deltakeravgift" };
        var notMatching = new ImportProfile { Name = "Annet", FullNameColumn = "Navn", AmountColumn = "Totalbeløp", GroupColumn = "Gruppe", CategoryColumn = "Deltakeravgift" };

        var (chosen, isSaved) = ImportProfileSuggester.ChooseProfile(headers, [notMatching, matching]);
        Assert.True(isSaved);
        Assert.Equal("Gruppe", chosen.Name);

        var (fallback, fallbackSaved) = ImportProfileSuggester.ChooseProfile(headers, [notMatching]);
        Assert.False(fallbackSaved);
        Assert.Equal(ImportProfileSuggester.SuggestedProfileName, fallback.Name);
    }

    [Theory]
    [InlineData("Kurshelg hosten 2026, Romsdal og Nordmore, Deltaker - deltakere.csv", "Kurshelg hosten 2026")]
    [InlineData("Vintertreff 2027 - deltakere.xlsx", "Vintertreff 2027")]
    [InlineData("projectParticipantsReportFull_20261120_1857.csv", null)]
    [InlineData("", null)]
    public void SuggestEventTitle_FromFileName(string fileName, string? expected)
    {
        Assert.Equal(expected, ImportProfileSuggester.SuggestEventTitle(fileName));
    }
}

public sealed class ParticipantImportMapperTests
{
    private static TabularData Individual(params string[] rows)
        => CsvTableReader.Parse(string.Join("\n", [ImportTestData.IndividualHeader, .. rows]));

    private static TabularData GroupReport(params string[] rows)
        => CsvTableReader.Parse(string.Join("\n", [ImportTestData.GroupReportHeader, .. rows]));

    [Fact]
    public void Map_IndividualExport_MapsFieldsCancellationAndAnswers()
    {
        var table = Individual(
            ImportTestData.IndividualRow("900001", "Ola Testesen", "Molde speidergruppe", "", "Deltaker (450 kr)", "450", formEmail: "ola@example.no", allergy: "Nøtter"),
            ImportTestData.IndividualRow("900002", "Kari Mellom Prøvesen", "Aukra speidergruppe", "kari@example.no", "Deltaker (450 kr)", "450", cancelledDate: "2026-10-20"),
            ImportTestData.IndividualRow("", "", "Molde speidergruppe", "", "", ""));

        var result = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Kurshelg høsten 2026");
        var ev = result.Event;

        Assert.Equal("import:kurshelg-hosten-2026", ev.ProjectId);
        Assert.Equal("Kurshelg høsten 2026", ev.EventTitle);
        Assert.Equal(2, ev.Participants.Count);
        Assert.Equal(1, result.SkippedRowCount);
        Assert.Contains(result.Warnings, w => w.Contains("Rad 4"));

        var kari = ev.Participants.Single(p => p.MemberNo == 900002);
        Assert.Equal("Kari Mellom", kari.FirstName);
        Assert.Equal("Prøvesen", kari.LastName);
        Assert.True(kari.Cancelled);
        Assert.Equal("kari@example.no", kari.Email);
        Assert.Equal("m:900002", kari.ParticipantKey);

        var ola = ev.Participants.Single(p => p.MemberNo == 900001);
        Assert.False(ola.Cancelled);
        Assert.True(ola.Confirmed);
        Assert.True(ola.Attended);
        Assert.Equal("ola@example.no", ola.Email); // fra skjemaets e-postsvar når «E-post» er tom
        Assert.Empty(ola.Answers); // skjemasvar tas ikke med som standard
        Assert.Equal("Molde speidergruppe", ola.GroupName);
        Assert.Equal("Fil", ola.AvailableGroups.Single().SourceLabel);

        var category = Assert.Single(ev.FeeCategories);
        Assert.Equal("Deltaker (450 kr)", category.Label);
        Assert.Equal(450m, category.SuggestedPriceNok);
        Assert.Equal(1, category.ParticipantCount);
    }

    [Fact]
    public void Map_SameCategoryWithDifferentAmounts_SplitsCategory()
    {
        var table = Individual(
            ImportTestData.IndividualRow("1", "A Test", "Molde", "", "Speider", "450"),
            ImportTestData.IndividualRow("2", "B Test", "Molde", "", "Speider", "300"),
            ImportTestData.IndividualRow("3", "C Test", "Molde", "", "Leder", "250"));

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        Assert.Equal(3, ev.FeeCategories.Count);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Speider (450 kr)" && c.SuggestedPriceNok == 450m);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Speider (300 kr)" && c.SuggestedPriceNok == 300m);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Leder" && c.SuggestedPriceNok == 250m);
    }

    [Fact]
    public void Map_ExplicitExtraColumns_BecomeAnswers()
    {
        var table = Individual(
            ImportTestData.IndividualRow("900001", "Ola Testesen", "Molde speidergruppe", "ola@example.no", "Deltaker (450 kr)", "450"));
        var profile = ImportProfileSuggester.Suggest(table.Headers);
        profile.ExtraColumns = ["Generelt - Hvilket kurs skal du delta på?"];

        var ola = ParticipantImportMapper.Map(table, profile, "Kurshelg").Event.Participants.Single();

        Assert.Equal("Peff-kurs", ola.Answers["Hvilket kurs skal du delta på?"]);
    }

    [Fact]
    public void Map_AmountOnly_And_NoPriceColumns()
    {
        var amountOnly = CsvTableReader.Parse("Navn;Beløp\nOla Test;450\nKari Test;450\nPer Test;0\n");
        var ev = ParticipantImportMapper.Map(amountOnly, ImportProfileSuggester.Suggest(amountOnly.Headers), "Test").Event;
        Assert.Equal(2, ev.FeeCategories.Count);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Deltakeravgift 450 kr" && c.ParticipantCount == 2);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Deltakeravgift 0 kr" && c.SuggestedPriceNok == 0m);

        var noPrice = CsvTableReader.Parse("Navn\nOla Test\n");
        var ev2 = ParticipantImportMapper.Map(noPrice, ImportProfileSuggester.Suggest(noPrice.Headers), "Test").Event;
        var cat = Assert.Single(ev2.FeeCategories);
        Assert.Equal("Deltakeravgift", cat.Label);
        Assert.Null(cat.SuggestedPriceNok);
        Assert.Equal("Uten gruppe", ev2.Participants.Single().GroupName);
    }

    [Fact]
    public void Map_GroupReport_ConfirmedAttendedAmountAndGroupId()
    {
        var table = GroupReport(
            ImportTestData.GroupReportRow("1501", "Molde speidergruppe", "900010", "Ola Testesen", "Speider", amount: "450,00"),
            ImportTestData.GroupReportRow("1501", "Molde speidergruppe", "900011", "Kari Testesen", "Speider", amount: "450,00", confirmed: "Nei", present: "Nei"),
            ImportTestData.GroupReportRow("1502", "Aukra speidergruppe", "900012", "Per Prøve", "Standardpris", amount: "0,00"));

        var result = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Vintertreff");
        var ev = result.Event;

        Assert.Equal(3, ev.Participants.Count);
        Assert.Empty(result.Warnings);
        Assert.All(ev.Participants, p => Assert.False(p.Cancelled));
        Assert.True(ev.Participants.Single(p => p.MemberNo == 900010).Confirmed);
        var kari = ev.Participants.Single(p => p.MemberNo == 900011);
        Assert.False(kari.Confirmed);
        Assert.False(kari.Attended);
        Assert.Equal(1501, kari.GroupId);
        Assert.Equal("900011@example.no", kari.Email);

        Assert.Contains(ev.FeeCategories, c => c.Label == "Speider" && c.SuggestedPriceNok == 450m && c.ParticipantCount == 2);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Standardpris" && c.SuggestedPriceNok == 0m && c.ParticipantCount == 1);
    }

    [Fact]
    public void Map_GroupReport_ZeroAmountFallsBackToPriceInFeeLabel()
    {
        // Fullrapporten har ofte «Antall» = «0,00» selv om avgiften koster noe; prisen i avgiftsnavnet vinner da,
        // som i MinSpeidingParser.DetectPriceNok.
        var table = GroupReport(
            ImportTestData.GroupReportRow("1501", "Molde", "900040", "Ola Test", "Lederkurs: 700.00", amount: "0,00"),
            ImportTestData.GroupReportRow("1501", "Molde", "900041", "Kari Test", "Standardpris", amount: "0,00"),
            ImportTestData.GroupReportRow("1501", "Molde", "900042", "Per Test", "Lederkurs: 700.00", amount: "650,00"));

        var participants = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event.Participants;

        Assert.Equal(700m, participants.Single(p => p.MemberNo == 900040).DetectedPriceNok);
        Assert.Equal(0m, participants.Single(p => p.MemberNo == 900041).DetectedPriceNok);
        Assert.Equal(650m, participants.Single(p => p.MemberNo == 900042).DetectedPriceNok);
    }

    [Fact]
    public void Map_DefaultCategoryKeyDoesNotCollideWithCategoryNamedAvgift()
    {
        var table = CsvTableReader.Parse("Navn;Kategori;Pris\nA Test;;450\nB Test;Avgift;450\nC Test;Avgift;300\n");

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        Assert.Equal(3, ev.FeeCategories.Count);
        Assert.Equal(3, ev.Participants.Select(p => p.FeeCategoryKey).Distinct().Count());
    }

    [Fact]
    public void Map_QuantityColumn_ExpandsZeroSkipsAndInvalidWarns()
    {
        var table = CsvTableReader.Parse(
            "Medlemsnummer;Navn;Deltakeravgift;Antall plasser\n"
            + "900012;Per Prøve;Leder (250 kr);3\n"
            + "900020;Ola Null;Speider (450 kr);0\n"
            + "900021;Kari Minus;Speider (450 kr);-1\n"
            + "900022;Per Tekst;Speider (450 kr);2 stk\n");
        var profile = ImportProfileSuggester.Suggest(table.Headers);
        Assert.Equal("Antall plasser", profile.QuantityColumn);

        var result = ParticipantImportMapper.Map(table, profile, "Test");
        var ev = result.Event;

        Assert.Equal(5, ev.Participants.Count);
        Assert.Equal(1, result.SkippedRowCount);
        Assert.DoesNotContain(ev.Participants, p => p.MemberNo == 900020);
        Assert.Contains(result.Warnings, w => w.Contains("Antall er 0"));
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("Ugyldig antall")));

        var per = ev.Participants.Where(p => p.MemberNo == 900012).ToList();
        Assert.Equal(["m:900012", "m:900012#2", "m:900012#3"], per.Select(p => p.ParticipantKey).Order());
        Assert.Contains(per, p => p.LastName == "Prøve (2/3)");
        Assert.Contains(result.Warnings, w => w.Contains("Antall 3"));
        Assert.Contains(ev.FeeCategories, c => c.Label == "Leder (250 kr)" && c.SuggestedPriceNok == 250m && c.ParticipantCount == 3);
    }

    [Fact]
    public void Map_MinSpeidingPlaceholdersAndColonPriceLabel()
    {
        // Samme form som en ekte rad fra Min speiding: «-» for tomme felt og avgift som «Navn: 700.00».
        var table = Individual(
            ImportTestData.IndividualRow("900030", "Ola Testesen", "Molde speidergruppe", "ola@example.no", "Lederkurs: 700.00", "700",
                formEmail: "ola.skjema@example.no"));
        var profile = ImportProfileSuggester.Suggest(table.Headers);

        var result = ParticipantImportMapper.Map(table, profile, "Kurshelg");
        var ola = Assert.Single(result.Event.Participants);

        Assert.False(ola.Cancelled);
        Assert.Equal("ola@example.no", ola.Email);
        Assert.Equal(700m, ola.DetectedPriceNok);
        Assert.Contains(result.Event.FeeCategories, c => c.Label == "Lederkurs: 700.00" && c.SuggestedPriceNok == 700m);

        profile.AmountColumn = null;
        var fromLabel = ParticipantImportMapper.Map(table, profile, "Kurshelg").Event.Participants.Single();
        Assert.Equal(700m, fromLabel.DetectedPriceNok);
    }

    [Fact]
    public void Map_TotalAmountIsDividedByQuantity()
    {
        var table = CsvTableReader.Parse("Navn;Gruppe;Deltakeravgift;Antall plasser;Totalbeløp\nOla Test;Molde;Deltaker;3;1350\n");
        var profile = ImportProfileSuggester.Suggest(table.Headers);
        Assert.True(profile.AmountIsTotal);

        var ev = ParticipantImportMapper.Map(table, profile, "Test").Event;

        Assert.Equal(3, ev.Participants.Count);
        Assert.All(ev.Participants, p => Assert.Equal(450m, p.DetectedPriceNok));
        Assert.Equal(450m, Assert.Single(ev.FeeCategories).SuggestedPriceNok);
    }

    [Fact]
    public void Map_CategoriesWithSameSlugAreNotMerged()
    {
        var table = CsvTableReader.Parse("Navn;Deltakeravgift;Pris\nA Test;Dag 1 + 2;500\nB Test;Dag 1 / 2;250\n");

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        Assert.Equal(2, ev.FeeCategories.Count);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Dag 1 + 2" && c.SuggestedPriceNok == 500m);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Dag 1 / 2" && c.SuggestedPriceNok == 250m);
    }

    [Fact]
    public void Map_CancelledRowWithZeroAmountDoesNotSplitCategory()
    {
        var table = Individual(
            ImportTestData.IndividualRow("1", "A Test", "Molde", "", "Deltaker (450 kr)", "450"),
            ImportTestData.IndividualRow("2", "B Test", "Molde", "", "Deltaker (450 kr)", "450"),
            ImportTestData.IndividualRow("3", "C Avmeldt", "Molde", "", "Deltaker (450 kr)", "0", cancelledDate: "2026-10-20"));

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        var category = Assert.Single(ev.FeeCategories);
        Assert.Equal("Deltaker (450 kr)", category.Label);
        Assert.Equal("import:deltaker-450-kr", category.Key);
        Assert.Equal(450m, category.SuggestedPriceNok);
    }

    [Fact]
    public void Map_MixedBlankAndFilledAmountsSplitByEffectivePrice()
    {
        var table = Individual(
            ImportTestData.IndividualRow("1", "A Test", "Molde", "", "Speider (450 kr)", ""),
            ImportTestData.IndividualRow("2", "B Test", "Molde", "", "Speider (450 kr)", "300"));

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        Assert.Equal(2, ev.FeeCategories.Count);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Speider (450 kr)" && c.SuggestedPriceNok == 450m);
        Assert.Contains(ev.FeeCategories, c => c.Label == "Speider (450 kr) (300 kr)" && c.SuggestedPriceNok == 300m);
    }

    [Fact]
    public void Map_MemberNoZeroUsesHashKey_AndUnknownGroupHasNoGroupOptions()
    {
        var table = CsvTableReader.Parse("Medlemsnummer;Navn\n0;Anne Ekstern\n0;Bjørn Ekstern\n");

        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test").Event;

        Assert.All(ev.Participants, p =>
        {
            Assert.Null(p.MemberNo);
            Assert.StartsWith("r:", p.ParticipantKey);
            Assert.Equal("Uten gruppe", p.GroupName);
            Assert.Empty(p.AvailableGroups);
        });
    }

    [Fact]
    public void Map_CheckedInCountsAsAttendedForFilter()
    {
        var header = "Navn;Gruppe;Innsjekket;Til stede\n";
        var table = CsvTableReader.Parse(header + "Ola Test;Molde;Ja;Nei\nKari Test;Molde;Nei;Nei\n");
        var profile = ImportProfileSuggester.Suggest(table.Headers);

        var ev = ParticipantImportMapper.Map(table, profile, "Test").Event;
        var ola = ev.Participants.Single(p => p.FirstName == "Ola");
        var kari = ev.Participants.Single(p => p.FirstName == "Kari");

        var filters = new ParticipantFilterOptions(RequireAttended: true);
        Assert.True(ola.CheckedIn);
        Assert.False(ola.Attended);
        Assert.True(InvoicePlanBuilder.IsParticipantIncluded(ola, filters, new Dictionary<string, ParticipantOverride>()));
        Assert.False(InvoicePlanBuilder.IsParticipantIncluded(kari, filters, new Dictionary<string, ParticipantOverride>()));

        // Bare «Innsjekket»: oppmøtt følger innsjekket i stedet for å være sann for alle.
        var onlyCheckedIn = CsvTableReader.Parse("Navn;Innsjekket\nOla Test;Nei\n");
        var p = ParticipantImportMapper.Map(onlyCheckedIn, ImportProfileSuggester.Suggest(onlyCheckedIn.Headers), "Test").Event.Participants.Single();
        Assert.False(p.Attended);
        Assert.False(p.CheckedIn);
    }

    [Fact]
    public void Map_WarnsAboutUnparseableAndNegativeAmounts()
    {
        var table = CsvTableReader.Parse("Navn;Pris\nOla Test;se kommentar\nKari Test;-450\nSum:;\n");

        var result = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Test");

        Assert.Equal(2, result.Event.Participants.Count);
        Assert.Contains(result.Warnings, w => w.Contains("se kommentar"));
        Assert.Contains(result.Warnings, w => w.Contains("Negativt beløp"));
    }

    [Theory]
    [InlineData("Testesen, Ola", "Ola", "Testesen")]
    [InlineData("Ola\u00A0Mellom Testesen", "Ola Mellom", "Testesen")]
    [InlineData("Cher", "Cher", "")]
    public void Map_SplitsFullName(string fullName, string first, string last)
    {
        var table = new TabularData(["Navn"], [[fullName]]);
        var p = ParticipantImportMapper.Map(table, new ImportProfile { FullNameColumn = "Navn" }, "Test").Event.Participants.Single();
        Assert.Equal(first, p.FirstName);
        Assert.Equal(last, p.LastName);
    }

    [Fact]
    public void Map_KeysAreStableAndUniqueWithoutMemberNo()
    {
        var csv = "Navn;E-post\nOla Test;ola@example.no\nOla Test;ola@example.no\nKari Test;\n";
        var table = CsvTableReader.Parse(csv);
        var profile = ImportProfileSuggester.Suggest(table.Headers);

        var first = ParticipantImportMapper.Map(table, profile, "Test").Event.Participants.Select(p => p.ParticipantKey).Order().ToList();
        var second = ParticipantImportMapper.Map(table, profile, "Test").Event.Participants.Select(p => p.ParticipantKey).Order().ToList();

        Assert.Equal(first, second);
        Assert.Equal(3, first.Distinct().Count());
        Assert.All(first, k => Assert.StartsWith("r:", k));
    }

    [Fact]
    public void Map_StatusValuesDefaultGroupAndSummaryRows()
    {
        var csv = "Fornavn;Etternavn;Status\nOla;Test;Påmeldt\nKari;Test;Avmeldt 12.10\nSum;;\n";
        var table = CsvTableReader.Parse(csv);
        var profile = ImportProfileSuggester.Suggest(table.Headers);
        profile.DefaultGroupName = "Molde speidergruppe";

        var ev = ParticipantImportMapper.Map(table, profile, "Test").Event;

        Assert.Equal(2, ev.Participants.Count);
        Assert.True(ev.Participants.Single(p => p.FirstName == "Kari").Cancelled);
        Assert.False(ev.Participants.Single(p => p.FirstName == "Ola").Cancelled);
        Assert.All(ev.Participants, p => Assert.Equal("Molde speidergruppe", p.GroupName));
    }

    [Fact]
    public void Map_WarnsAboutMissingColumnsAndRequiresName()
    {
        var table = CsvTableReader.Parse("Navn\nOla Test\n");
        var profile = new ImportProfile { FullNameColumn = "Navn", GroupColumn = "Speidergruppe" };

        var result = ParticipantImportMapper.Map(table, profile, "Test");
        Assert.Contains(result.Warnings, w => w.Contains("«Speidergruppe»"));

        Assert.Throws<InvalidOperationException>(() => ParticipantImportMapper.Map(table, new ImportProfile(), "Test"));
    }

    [Fact]
    public void Map_ThenBuildInvoicePlan_GroupsInvoicesPerScoutGroup()
    {
        var table = Individual(
            ImportTestData.IndividualRow("1", "Ola Testesen", "Molde speidergruppe", "", "Deltaker (450 kr)", "450"),
            ImportTestData.IndividualRow("2", "Ingrid Testesen", "Molde speidergruppe", "", "Leder (250 kr)", "250"),
            ImportTestData.IndividualRow("3", "Kari Prøvesen", "Aukra", "", "Deltaker (450 kr)", "450"),
            ImportTestData.IndividualRow("4", "Per Avmeldt", "Aukra", "", "Deltaker (450 kr)", "450", cancelledDate: "2026-10-20"));
        var ev = ParticipantImportMapper.Map(table, ImportProfileSuggester.Suggest(table.Headers), "Kurshelg").Event;

        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true, RequireConfirmed: true, RequireAttended: true),
            CategoryPricesNok: ev.FeeCategories.ToDictionary(c => c.Key, c => c.SuggestedPriceNok ?? 0m),
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>(),
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 24),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: "Kurshelg",
            OurReference: null,
            OrderReferencePrefix: null);
        List<FikenContact> customers =
        [
            new(1001, "Molde speidergruppe", "molde@example.no", null, 1, null, true, false, false, 14, null),
            new(1003, "Aukra", "aukra@example.no", null, 3, null, true, false, false, 14, null)
        ];

        var plan = InvoicePlanBuilder.Build(ev, options, customers, new Dictionary<string, long>());

        Assert.Equal(3, plan.IncludedParticipantCount);
        Assert.Equal(2, plan.Drafts.Count);
        Assert.Equal(70000, plan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe").TotalOre);
        var aukra = plan.Drafts.Single(d => d.RecipientName == "Aukra");
        Assert.Equal(45000, aukra.TotalOre);
        Assert.Equal(1003, aukra.SelectedContactId);
    }
}

public sealed class JsonImportProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fh-import-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveLoadReplaceAndDelete()
    {
        var store = new JsonImportProfileStore(_dir);
        Assert.Empty(await store.LoadAllAsync(TestContext.Current.CancellationToken));

        await store.SaveAsync(new ImportProfile { Name = "Min speiding individuell", FullNameColumn = "Navn", EmailColumns = ["E-post", "E-post (2)"] }, TestContext.Current.CancellationToken);
        await store.SaveAsync(new ImportProfile { Name = "Annen", FullNameColumn = "Name" }, TestContext.Current.CancellationToken);
        await store.SaveAsync(new ImportProfile { Name = "min speiding individuell ", FullNameColumn = "Navn", GroupColumn = "Gruppe" }, TestContext.Current.CancellationToken);

        var reloaded = await new JsonImportProfileStore(_dir).LoadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, reloaded.Count);
        var profile = reloaded.Single(p => p.FullNameColumn == "Navn");
        Assert.Equal("Gruppe", profile.GroupColumn);
        Assert.Empty(profile.EmailColumns);

        await store.DeleteAsync("ANNEN", TestContext.Current.CancellationToken);
        Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_dir, JsonImportProfileStore.FileName)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
