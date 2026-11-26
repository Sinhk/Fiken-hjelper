using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>
/// Lager Excel-filer (ClosedXML) av tilskudd. Beløp er ekte tall, datoer er ekte datoer, og manglende verdier er tomme celler
/// (aldri 0) med en forklaring i en merknadskolonne. Bare lesing: ingenting skrives til Fiken.
/// </summary>
public static class GrantWorkbookExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private const string AmountFormat = "#,##0.00;-#,##0.00";
    private const string DateFormat = "dd.mm.yyyy";
    private const int MaxFileNameLength = 80;
    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    // ---- Filnavn ----

    /// <summary>Filnavn for ett tilskudd: <c>Tilskudd &lt;navn&gt; &lt;dato&gt;.xlsx</c>, bare med trygge tegn.</summary>
    public static string FileName(Grant grant, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return $"Tilskudd {SafeName(grant.Name)} {today:yyyy-MM-dd}.xlsx";
    }

    /// <summary>Filnavn for oversikten, med år og status når filteret er satt.</summary>
    public static string OverviewFileName(int? year, GrantStatus? status, DateOnly today)
    {
        var parts = new List<string> { "Tilskudd" };
        parts.Add(year is { } y ? y.ToString(CultureInfo.InvariantCulture) : "alle år");
        if (status is { } s)
        {
            parts.Add(SafeName(Grant.StatusText(s)));
        }

        parts.Add(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return string.Join(' ', parts) + ".xlsx";
    }

    /// <summary>Bokstaver, tall, mellomrom, bindestrek og understrek. Alt annet (også tegn som er ugyldige i filnavn) blir mellomrom.</summary>
    internal static string SafeName(string? name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name ?? string.Empty)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : ' ');
        }

        var text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxFileNameLength)
        {
            text = text[..MaxFileNameLength].TrimEnd();
        }

        return text.Length == 0 ? "uten navn" : text;
    }

    // ---- Ett tilskudd ----

    public static byte[] ExportGrant(GrantReport report, IReadOnlyList<FikenProject>? projects, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var workbook = new XLWorkbook();
        WriteSummary(workbook.AddWorksheet("Sammendrag"), report, today);
        WriteByAccount(workbook.AddWorksheet("Bruk per konto"), report);
        WritePostings(workbook.AddWorksheet("Bilag"), report, projects);
        return Save(workbook);
    }

    private static void WriteSummary(IXLWorksheet ws, GrantReport report, DateOnly today)
    {
        var grant = report.Grant;
        var row = 1;
        ws.Cell(row, 1).Value = $"Tilskudd: {grant.DisplayName}";
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 14;
        row += 2;

        Header(ws, row++, "Felt", "Verdi", "Merknad");
        void Field(string label, object? value, string? note = null)
        {
            ws.Cell(row, 1).Value = label;
            SetValue(ws.Cell(row, 2), value);
            if (note is not null)
            {
                ws.Cell(row, 3).Value = note;
            }

            row++;
        }

        Field("Navn", grant.DisplayName);
        Field("Tilskuddsgiver", grant.Grantor);
        Field("Ordning", grant.Scheme);
        Field("Type", Grant.KindText(grant.Kind));
        Field("Status", Grant.StatusText(grant.Status));
        Field("Søknadsfrist", grant.ApplicationDeadline);
        Field("Søkt dato", grant.AppliedDate);
        Field("Vedtaksdato", grant.DecisionDate);
        Field("Rapporteringsfrist", grant.ReportDeadline);
        Field("Bruksperiode fra", grant.UseFrom);
        Field("Bruksperiode til", grant.UseTo);
        Field("Søkt beløp (kr)", grant.AppliedAmount, grant.AppliedAmount is null ? "Ikke satt" : null);
        Field("Lenke", grant.Link);
        Field("Notater", grant.Notes);
        Field("Eksportert", today);

        row++;
        ws.Cell(row++, 1).Value = "Nøkkeltall";
        ws.Cell(row - 1, 1).Style.Font.Bold = true;
        var received = GrantOverviewLogic.ReceivedOrNull(report);
        var remaining = GrantOverviewLogic.RemainingOrNull(report);
        var unused = received is null ? null : report.Unused;
        Field("Innvilget (kr)", report.Granted, report.Granted is null ? "Ikke satt" : null);
        Field("Mottatt (kr)", received, received is null ? "Mangler Fiken-data. Koblede utbetalinger kan ikke leses." : null);
        Field("Brukt (kr)", report.Used, GrantTextFormatter.UsedMissingText(report) is { } missing ? Capitalize(missing) : null);
        Field("Gjenstår å motta (kr)", remaining, remaining is null ? "Innvilget eller mottatt er ikke kjent." : null);
        Field("Ubrukt (mottatt − brukt) (kr)", unused, unused is null ? "Mottatt eller brukt er ikke kjent." : null);
        if (report.OtherIncome != 0m)
        {
            Field("Andre inntekter på prosjektet (kr)", report.OtherIncome, "Vises for seg og trekkes ikke fra brukt.");
        }

        row++;
        ws.Cell(row++, 1).Value = "Utbetalinger";
        ws.Cell(row - 1, 1).Style.Font.Bold = true;
        if (report.Payments.Count == 0)
        {
            ws.Cell(row, 1).Value = "Ingen utbetalinger er registrert.";
        }
        else
        {
            Header(ws, row++, "Kilde", "Beløp (kr)", "Dato", "Beskrivelse / merknad");
            foreach (var payment in report.Payments)
            {
                var unknown = payment.Payment.IsLinked && (payment.Missing || !report.HasFikenData);
                ws.Cell(row, 1).Value = payment.Payment.IsLinked ? "Fiken" : "Manuell";
                if (!unknown)
                {
                    SetValue(ws.Cell(row, 2), payment.Amount);
                }

                SetValue(ws.Cell(row, 3), payment.Date);
                var text = payment.Description ?? string.Empty;
                if (unknown)
                {
                    text += (text.Length > 0 ? " " : string.Empty)
                        + (payment.Missing ? "Finnes ikke i hentede posteringer. Beløpet er ikke regnet med." : "Mangler Fiken-data. Beløpet hentes fra Fiken.");
                }

                if (text.Length > 0)
                {
                    ws.Cell(row, 4).Value = text;
                }

                row++;
            }

            ws.Cell(row, 1).Value = "Sum";
            if (received is { } total)
            {
                ws.Cell(row, 2).Value = total;
            }
            else
            {
                ws.Cell(row, 4).Value = "Mangler Fiken-data, så summen er ikke kjent.";
            }

            ws.Range(row, 1, row, 4).Style.Font.Bold = true;
            ws.Range(row, 1, row, 4).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        ws.Columns(1, 4).AdjustToContents(8, 70);
    }

    private static void WriteByAccount(IXLWorksheet ws, GrantReport report)
    {
        var row = 1;
        ws.Cell(row++, 1).Value = "Bruk per konto";
        ws.Cell(row - 1, 1).Style.Font.Bold = true;
        ws.Cell(row - 1, 1).Style.Font.FontSize = 13;
        Header(ws, row++, "Konto", "Navn", "Sum (kr)");

        if (report.UsedByAccount.Count == 0 && report.ManualUsed == 0m)
        {
            ws.Cell(row++, 1).Value = GrantTextFormatter.UsedMissingText(report) switch
            {
                GrantTextFormatter.MissingFikenData => "Mangler Fiken-data, så bruken kan ikke vises.",
                GrantTextFormatter.NotRegistered => "Bruk er ikke registrert.",
                _ => "Ingen kostnader funnet i bruksperioden."
            };
        }
        else
        {
            row = AccountRows(ws, row, report.UsedByAccount, report.ManualUsed, report.Used ?? 0m);
        }

        if (report.OtherIncomeByAccount.Count > 0)
        {
            row++;
            ws.Cell(row++, 1).Value = "Andre inntekter på prosjektet (trekkes ikke fra brukt)";
            ws.Cell(row - 1, 1).Style.Font.Bold = true;
            Header(ws, row++, "Konto", "Navn", "Sum (kr)");
            AccountRows(ws, row, report.OtherIncomeByAccount, 0m, report.OtherIncome);
        }

        ws.Column(3).Style.NumberFormat.Format = AmountFormat;
        ws.SheetView.FreezeRows(2);
        ws.Columns(1, 3).AdjustToContents(8, 60);
    }

    private static int AccountRows(IXLWorksheet ws, int row, IReadOnlyList<GrantAccountSum> accounts, decimal manual, decimal total)
    {
        foreach (var account in accounts)
        {
            ws.Cell(row, 1).Value = account.Account;
            if (account.AccountName is not null)
            {
                ws.Cell(row, 2).Value = account.AccountName;
            }

            ws.Cell(row, 3).Value = account.Amount;
            row++;
        }

        if (manual != 0m)
        {
            ws.Cell(row, 2).Value = "Manuell bruk";
            ws.Cell(row, 3).Value = manual;
            row++;
        }

        ws.Cell(row, 1).Value = "Sum";
        ws.Cell(row, 3).Value = total;
        ws.Range(row, 1, row, 3).Style.Font.Bold = true;
        ws.Range(row, 1, row, 3).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        return row + 1;
    }

    private static void WritePostings(IXLWorksheet ws, GrantReport report, IReadOnlyList<FikenProject>? projects)
    {
        string[] headers =
        [
            "Dato", "Bilagsnr", "Beskrivelse", "Konto", "Kontonavn", "Prosjekt", "Andel (%)",
            "Beløp for tilskuddet (kr)", "Prosjektets fulle beløp (kr)", "Type"
        ];
        Header(ws, 1, headers);
        var projectNames = (projects ?? []).GroupBy(p => p.ProjectId).ToDictionary(g => g.Key, g => g.First().Name);
        var grant = report.Grant;
        var row = 2;

        foreach (var account in report.UsedByAccount)
        {
            foreach (var p in account.Postings)
            {
                Posting(p, account.AccountName, "Bruk", p.ProjectId is { } pid ? grant.ShareOf(pid) ?? 100m : null, p.Amount);
            }
        }

        foreach (var use in grant.ManualUses ?? [])
        {
            SetValue(ws.Cell(row, 1), use.Date);
            SetValue(ws.Cell(row, 3), use.Description);
            ws.Cell(row, 8).Value = use.Amount;
            ws.Cell(row, 10).Value = "Manuell bruk";
            row++;
        }

        foreach (var account in report.OtherIncomeByAccount)
        {
            foreach (var p in account.Postings)
            {
                // Annen inntekt trekkes ikke fra brukt og har ingen andel, så bare det fulle beløpet vises.
                Posting(p, account.AccountName, "Annen inntekt", null, null);
            }
        }

        void Posting(GrantPosting p, string? accountName, string type, decimal? share, decimal? amount)
        {
            SetValue(ws.Cell(row, 1), GrantReportBuilder.ParseDate(p.Date));
            SetValue(ws.Cell(row, 2), p.JournalEntryNumber);
            SetValue(ws.Cell(row, 3), p.Description);
            ws.Cell(row, 4).Value = p.Account;
            SetValue(ws.Cell(row, 5), accountName);
            if (p.ProjectId is { } id)
            {
                ws.Cell(row, 6).Value = projectNames.GetValueOrDefault(id) ?? $"Prosjekt {id}";
            }

            SetValue(ws.Cell(row, 7), share);
            SetValue(ws.Cell(row, 8), amount);
            ws.Cell(row, 9).Value = p.FullAmount;
            ws.Cell(row, 10).Value = type;
            row++;
        }

        if (row == 2)
        {
            ws.Cell(row, 1).Value = GrantTextFormatter.UsedMissingText(report) is { } missing
                ? $"Ingen bilag: {missing}."
                : "Ingen bilag funnet i bruksperioden.";
        }

        ws.Column(1).Style.NumberFormat.Format = DateFormat;
        ws.Column(7).Style.NumberFormat.Format = "General\" %\"";
        ws.Column(8).Style.NumberFormat.Format = AmountFormat;
        ws.Column(9).Style.NumberFormat.Format = AmountFormat;
        if (row > 2)
        {
            ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents(8, 60);
    }

    // ---- Oversikt ----

    /// <param name="rows">De synlige tilskuddene (etter filter).</param>
    /// <param name="filterText">Beskrivelse av filteret, eller null når alt er med.</param>
    public static byte[] ExportOverview(IReadOnlyList<GrantReport> rows, DateOnly today, string? filterText = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        using var workbook = new XLWorkbook();
        WriteOverview(workbook.AddWorksheet("Tilskudd"), rows, today);
        WriteOverviewNotes(workbook.AddWorksheet("Merknader"), today, filterText);
        return Save(workbook);
    }

    private static void WriteOverview(IXLWorksheet ws, IReadOnlyList<GrantReport> rows, DateOnly today)
    {
        string[] headers =
        [
            "Navn", "Tilskuddsgiver", "Ordning", "Type", "Status", "Søkt (kr)", "Innvilget (kr)", "Mottatt (kr)", "Brukt (kr)",
            "Gjenstår å motta (kr)", "Søknadsfrist", "Rapporteringsfrist", "Frister som haster", "Merknad"
        ];
        Header(ws, 1, headers);
        var row = 2;
        foreach (var r in rows)
        {
            var grant = r.Grant;
            var received = GrantOverviewLogic.ReceivedOrNull(r);
            var remaining = GrantOverviewLogic.RemainingOrNull(r);
            ws.Cell(row, 1).Value = grant.DisplayName;
            SetValue(ws.Cell(row, 2), grant.Grantor);
            SetValue(ws.Cell(row, 3), grant.Scheme);
            ws.Cell(row, 4).Value = Grant.KindText(grant.Kind);
            ws.Cell(row, 5).Value = Grant.StatusText(grant.Status);
            SetValue(ws.Cell(row, 6), grant.AppliedAmount);
            SetValue(ws.Cell(row, 7), r.Granted);
            SetValue(ws.Cell(row, 8), received);
            SetValue(ws.Cell(row, 9), r.Used);
            SetValue(ws.Cell(row, 10), remaining);
            SetValue(ws.Cell(row, 11), grant.ApplicationDeadline);
            SetValue(ws.Cell(row, 12), grant.ReportDeadline);
            var deadlines = GrantOverviewLogic.Deadlines(grant, today).Select(DeadlineText).ToList();
            if (deadlines.Count > 0)
            {
                ws.Cell(row, 13).Value = string.Join("; ", deadlines);
            }

            var notes = new List<string>();
            if (received is null)
            {
                notes.Add("Mottatt: mangler Fiken-data");
            }

            if (GrantTextFormatter.UsedMissingText(r) is { } missing)
            {
                notes.Add($"Brukt: {missing}");
            }

            if (notes.Count > 0)
            {
                ws.Cell(row, 14).Value = string.Join("; ", notes);
            }

            row++;
        }

        var totals = GrantOverviewLogic.Totals(rows);
        ws.Cell(row, 1).Value = $"Sum ({rows.Count} tilskudd)";
        TotalCell(ws.Cell(row, 6), totals.Applied);
        TotalCell(ws.Cell(row, 7), totals.Granted);
        TotalCell(ws.Cell(row, 8), totals.Received);
        TotalCell(ws.Cell(row, 9), totals.Used);
        TotalCell(ws.Cell(row, 10), totals.Remaining);
        if (GrantOverviewLogic.PartialNotes(totals) is { Count: > 0 } partial)
        {
            ws.Cell(row, 14).Value = $"Delsum, siden noen tilskudd mangler verdi: {string.Join("; ", partial)}.";
        }

        ws.Row(row).Style.Font.Bold = true;
        ws.Range(row, 1, row, headers.Length).Style.Border.TopBorder = XLBorderStyleValues.Thin;

        ws.Range(2, 6, row, 10).Style.NumberFormat.Format = AmountFormat;
        ws.Range(2, 11, row - 1, 12).Style.NumberFormat.Format = DateFormat;
        if (rows.Count > 0)
        {
            ws.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
        }

        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);
        ws.Columns(1, headers.Length).AdjustToContents(8, 60);
    }

    private static void TotalCell(IXLCell cell, GrantTotal total)
    {
        if (total.HasValue)
        {
            cell.Value = total.Sum;
        }
    }

    private static void WriteOverviewNotes(IXLWorksheet ws, DateOnly today, string? filterText)
    {
        var lines = new List<string>
        {
            $"Eksportert {today.ToString("dd.MM.yyyy", Nb)}.",
            string.IsNullOrWhiteSpace(filterText) ? "Utvalg: alle tilskudd." : $"Utvalg: {filterText.Trim()}.",
            "Tomme celler betyr at verdien mangler eller ikke er kjent. Det er ikke det samme som 0.",
            "Brukt er brutto kostnader (konto 4000–7999 og 8100–8799) på de koblede prosjektene i bruksperioden, ganget med tilskuddets andel.",
            "Gjenstår å motta = innvilget − mottatt.",
            "Mottatt og brukt hentes fra Fiken. Uten Fiken-data er mottatt tom for koblede utbetalinger, og brukt er tom med forklaring i kolonnen Merknad.",
            "Summene teller bare tilskudd som har verdi. Er noen utelatt, står det i merknaden på sumraden."
        };
        for (var i = 0; i < lines.Count; i++)
        {
            ws.Cell(i + 1, 1).Value = lines[i];
        }

        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Column(1).AdjustToContents(20, 120);
    }

    private static string DeadlineText(GrantDeadline d) => d.State == GrantDeadlineState.Overdue
        ? $"{d.Label} {d.Date.ToString("dd.MM.yyyy", Nb)} er passert"
        : d.Days == 0
            ? $"{d.Label} i dag"
            : $"{d.Label} {d.Date.ToString("dd.MM.yyyy", Nb)} (om {d.Days} d)";

    // ---- Hjelpere ----

    private static void Header(IXLWorksheet ws, int row, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(row, i + 1).Value = headers[i];
        }

        var range = ws.Range(row, 1, row, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    /// <summary>Skriver verdien som riktig celletype. Null og tom tekst gir en tom celle.</summary>
    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string s when string.IsNullOrWhiteSpace(s):
                return;
            case string s:
                cell.Value = s;
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.NumberFormat.Format = DateFormat;
                break;
            case decimal m:
                cell.Value = m;
                cell.Style.NumberFormat.Format = AmountFormat;
                break;
            case int i:
                cell.Value = i;
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Nb) + text[1..] + ".";

    private static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
