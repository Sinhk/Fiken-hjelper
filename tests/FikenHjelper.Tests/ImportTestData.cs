namespace FikenHjelper.Tests;

/// <summary>
/// Syntetiske eksportfiler med samme kolonneoppsett som Min speiding sine CSV-eksporter.
/// Alle navn, medlemsnumre og e-poster er oppdiktet.
/// </summary>
internal static class ImportTestData
{
    /// <summary>Overskrifter i «&lt;Arrangement&gt;, Deltaker - deltakere.csv» (individuell påmelding).</summary>
    public const string IndividualHeader =
        "\"Medlemsnummer\",\"Navn\",\"Kjønn\",\"Fødselsdato\",\"Organisasjon\",\"Krets\",\"Korps\",\"Gruppe\",\"Enhet\",\"Patrulje\",\"E-post\",\"Adresse\",\"By\",\"Postnummer\",\"Land\",\"Telefonnr.\",\"Samtykke for bildebruk\",\"Foresatt 1 e-post\",\"Foresatt 2 e-post\",\"Mors e-post\",\"Fars e-post\",\"E-post2\",\"E-post\",\"Påmeldt\",\"Påmeldtdato\",\"Avmeldingsdato\",\"Betalt deltakeravgift\",\"Betalt for 2026\",\"Har roller som krever politiattest.\",\"Status for politiattest\",\"Deltakeravgift\",\"Totalbeløp\",\"Generelt - Hvilket kurs skal du delta på?\",\"Generelt - Mobilnummer\",\"Generelt - E-postadresse\",\"Generelt - Hvor lenge har du vært leder?\",\"Generelt - I hvilken enhet er du leder?\",\"Generelt - Hva kunne du spesielt tenke deg å få lære mer om på kurset? \",\"Generelt - Er du Peff eller Ass?\",\"Generelt - Hva heter patruljen din?\",\"Generelt - Har deltakeren allergier eller diettbehov vi må ta hensyn til?\",\"Generelt - Beskrivelse av allergi/diettbehov\",\"Generelt - Er det noe annet vi bør vite?\"";

    /// <summary>Overskrifter i «projectParticipantsReportFull_*.csv» (gruppe-/fullrapport).</summary>
    public const string GroupReportHeader =
        "\"Krets\",\"Korps\",\"Gruppenummer\",\"Gruppe\",\"Aktivitetspatrulje\",\"Medlemsnummer\",\"Navn\",\"Kjønn\",\"Fødselsdato\",\"E-post\",\"Adresse\",\"By\",\"Postnummer\",\"Land\",\"Mobiltelefon\",\"Foresatt 1 mobil\",\"Foresatt 1 e-post\",\"Foresatt 2 mobil\",\"Foresatt 2 e-post\",\"Samtykke for bildebruk\",\"Betalt for Ingen tidligere termin\",\"Betalt for Ingen nåværende termin\",\"Har roller som krever politiattest.\",\"Status for politiattest\",\"Innsjekket\",\"Tidligste innsjekking\",\"Siste utsjekking\",\"Til stede\",\"Påmeldt\",\"Påmeldtdato\",\"Bekreftet\",\"Avmeldingsdato\",\"Deltakeravgift\",\"Antall\",\"Delegatnummer\",\"Gruppepåmelding\",\"Individuell påmelding\"";

    public static string IndividualRow(
        string memberNo,
        string name,
        string group,
        string email,
        string fee,
        string total,
        string cancelledDate = "",
        string course = "Peff-kurs",
        string allergy = "",
        string formEmail = "")
    {
        var cells = new string[43];
        Array.Fill(cells, string.Empty);
        cells[0] = memberNo;
        cells[1] = name;
        cells[5] = "Romsdal og Nordmøre krets";
        cells[7] = group;
        cells[10] = email;
        cells[23] = "Ja";
        cells[24] = "2026-10-01";
        cells[25] = cancelledDate;
        cells[30] = fee;
        cells[31] = total;
        cells[32] = course;
        cells[34] = formEmail;
        cells[41] = allergy;
        return string.Join(',', cells.Select(Quote));
    }

    public static string GroupReportRow(
        string groupNo,
        string group,
        string memberNo,
        string name,
        string fee,
        string quantity = "1",
        string confirmed = "Ja",
        string present = "Ja",
        string cancelledDate = "")
    {
        var cells = new string[37];
        Array.Fill(cells, string.Empty);
        cells[0] = "Romsdal og Nordmøre krets";
        cells[2] = groupNo;
        cells[3] = group;
        cells[5] = memberNo;
        cells[6] = name;
        cells[9] = $"{memberNo}@example.no";
        cells[27] = present;
        cells[28] = "Ja";
        cells[30] = confirmed;
        cells[31] = cancelledDate;
        cells[32] = fee;
        cells[33] = quantity;
        return string.Join(',', cells.Select(Quote));
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
