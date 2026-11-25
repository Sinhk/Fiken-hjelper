using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ClosedXML.Excel;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Budgeting;

public sealed record BudgetImportResult(
    IReadOnlyList<BudgetYear> Years,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Leser kretsens oppsett "Sammenstilling av budsjett og regnskap":
/// <list type="bullet">
/// <item>Et oversiktsark med kolonnene "Budsjett ÅÅÅÅ" / "Regnskap ÅÅÅÅ" og én rad per budsjettpost.</item>
/// <item>Valgfrie ark "Regnskap ÅÅÅÅ" med Fiken-prosjekt (kol. A) og budsjettpost (kol. C).</item>
/// </list>
/// År uten eget prosjektark får regnskapstallet fra oversikten som manuelt tall.
/// </summary>
public static partial class BudgetWorkbookImporter
{
    [GeneratedRegex(@"^\s*Budsjett\s+(\d{4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BudgetHeader();

    [GeneratedRegex(@"^\s*Regnskap\s+(\d{4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ActualHeader();

    private const int MaxHeaderRow = 5;
    private const int MaxZipEntries = 10_000;
    private const long MaxUncompressedBytes = 200L * 1024 * 1024;

    public static BudgetImportResult Import(Stream xlsx, IReadOnlyList<FikenProject> fikenProjects)
    {
        ArgumentNullException.ThrowIfNull(xlsx);
        ArgumentNullException.ThrowIfNull(fikenProjects);

        using var sanitized = RemoveInvalidRelationships(xlsx);
        using var workbook = new XLWorkbook(sanitized);
        var warnings = new List<string>();

        var (overview, headerRow, budgetCols, actualCols) = FindOverview(workbook)
            ?? throw new InvalidDataException(
                "Fant ikke et ark med kolonneoverskrifter som «Budsjett 2026». Sjekk at riktig fil er valgt.");

        var years = new Dictionary<int, BudgetYear>();
        var lastRow = overview.LastRowUsed()?.RowNumber() ?? headerRow;

        for (var row = headerRow + 1; row <= lastRow; row++)
        {
            var name = overview.Cell(row, 1).GetString().Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("Resultat", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (year, col) in budgetCols)
            {
                var budget = ReadNumber(overview.Cell(row, col));
                var actual = actualCols.TryGetValue(year, out var actualCol)
                    ? ReadNumber(overview.Cell(row, actualCol))
                    : null;
                if (budget is null && actual is null)
                {
                    continue;
                }

                if (!years.TryGetValue(year, out var budgetYear))
                {
                    budgetYear = new BudgetYear { Year = year };
                    years[year] = budgetYear;
                }

                if (budgetYear.Lines.Any(l => Normalize(l.Name) == Normalize(name)))
                {
                    warnings.Add($"{year}: Posten «{name}» står flere ganger i oversikten. Bare den første er tatt med.");
                    continue;
                }

                budgetYear.Lines.Add(new BudgetLine
                {
                    Name = name,
                    Amount = budget ?? 0m,
                    ManualActual = actual
                });
            }
        }

        var projectsByName = fikenProjects
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => Normalize(p.Name))
            .ToDictionary(g => g.Key, g => g.Select(p => p.ProjectId).ToList());

        foreach (var budgetYear in years.Values)
        {
            var sheetName = $"Regnskap {budgetYear.Year}";
            if (!workbook.TryGetWorksheet(sheetName, out var projectSheet))
            {
                warnings.Add($"{budgetYear.Year}: Fant ikke arket «{sheetName}». Regnskapstallene for året er lagt inn som manuelle tall.");
                continue;
            }

            if (fikenProjects.Count == 0)
            {
                warnings.Add($"{budgetYear.Year}: Ingen Fiken-prosjekter er lastet, så prosjektkoblingene ble ikke importert. Regnskapstallene er lagt inn som manuelle tall.");
                continue;
            }

            // Året har prosjektkobling: regnskap hentes fra Fiken i stedet for manuelle tall.
            foreach (var line in budgetYear.Lines)
            {
                line.ManualActual = null;
            }

            ImportProjectSheet(projectSheet, budgetYear, projectsByName, warnings);
        }

        return new BudgetImportResult(
            years.Values.OrderByDescending(y => y.Year).ToList(),
            warnings);
    }

    private static void ImportProjectSheet(
        IXLWorksheet sheet,
        BudgetYear budgetYear,
        Dictionary<string, List<long>> projectsByName,
        List<string> warnings)
    {
        var linesByName = budgetYear.Lines.ToDictionary(l => Normalize(l.Name));
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;

        for (var row = 2; row <= lastRow; row++)
        {
            var projectName = sheet.Cell(row, 1).GetString().Trim();
            var postName = sheet.Cell(row, 3).GetString().Trim();
            if (string.IsNullOrEmpty(projectName))
            {
                continue;
            }

            if (string.IsNullOrEmpty(postName))
            {
                warnings.Add($"{budgetYear.Year}: Prosjektet «{projectName}» mangler budsjettpost i arket.");
                continue;
            }

            if (!linesByName.TryGetValue(Normalize(postName), out var line))
            {
                warnings.Add($"{budgetYear.Year}: Budsjettposten «{postName}» (for prosjektet «{projectName}») finnes ikke i oversikten.");
                continue;
            }

            if (!projectsByName.TryGetValue(Normalize(projectName), out var ids))
            {
                warnings.Add($"{budgetYear.Year}: Fant ikke prosjektet «{projectName}» i Fiken. Koble det manuelt til «{line.Name}».");
                continue;
            }

            if (ids.Count > 1)
            {
                warnings.Add($"{budgetYear.Year}: Flere Fiken-prosjekter heter «{projectName}». Alle er koblet til «{line.Name}», så sjekk koblingen.");
            }

            foreach (var id in ids.Where(id => !line.ProjectIds.Contains(id)))
            {
                line.ProjectIds.Add(id);
            }
        }
    }

    private static (IXLWorksheet Sheet, int HeaderRow, Dictionary<int, int> BudgetCols, Dictionary<int, int> ActualCols)? FindOverview(XLWorkbook workbook)
    {
        foreach (var sheet in workbook.Worksheets)
        {
            for (var row = 1; row <= MaxHeaderRow; row++)
            {
                var budgetCols = new Dictionary<int, int>();
                var actualCols = new Dictionary<int, int>();
                foreach (var cell in sheet.Row(row).CellsUsed())
                {
                    var text = cell.GetString();
                    var b = BudgetHeader().Match(text);
                    if (b.Success)
                    {
                        budgetCols[int.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture)] = cell.Address.ColumnNumber;
                        continue;
                    }

                    var a = ActualHeader().Match(text);
                    if (a.Success)
                    {
                        actualCols[int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture)] = cell.Address.ColumnNumber;
                    }
                }

                if (budgetCols.Count > 0)
                {
                    return (sheet, row, budgetCols, actualCols);
                }
            }
        }

        return null;
    }

    private static decimal? ReadNumber(IXLCell cell)
    {
        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
        if (cell.HasFormula && value.IsBlank)
        {
            // Filer lagret uten beregnede formelverdier: prøv å regne ut formelen selv.
            try
            {
                value = cell.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (value.IsNumber)
        {
            return Math.Round((decimal)value.GetNumber(), 2);
        }

        return value.IsText ? ParseNumber(value.GetText()) : null;
    }

    /// <summary>
    /// Tolker tall skrevet som tekst i et norsk ark, f.eks. «1 500», «−1 234,50», «kr 1 500,-» og «10.000».
    /// Komma er desimalskille. Når både punktum og komma forekommer, er det siste av dem desimalskillet.
    /// Et skilletegn som forekommer flere ganger, eller et enkelt punktum med nøyaktig tre sifre etter,
    /// er tusenskille.
    /// </summary>
    public static decimal? ParseNumber(string raw)
    {
        var text = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray())
            .Replace('\u2212', '-')
            .Replace("kr.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("kr", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("NOK", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (text.EndsWith(",-", StringComparison.Ordinal) || text.EndsWith(".-", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        if (text.Length == 0)
        {
            return null;
        }

        var commas = text.Count(c => c == ',');
        var dots = text.Count(c => c == '.');
        if (commas > 0 && dots > 0)
        {
            var decimalSeparator = text.LastIndexOf(',') > text.LastIndexOf('.') ? ',' : '.';
            var groupSeparator = decimalSeparator == ',' ? '.' : ',';
            if (text.Count(c => c == decimalSeparator) > 1)
            {
                return null;
            }

            text = text.Replace(groupSeparator.ToString(), string.Empty).Replace(',', '.');
        }
        else if (commas > 1 || dots > 1)
        {
            // «1.234.567» er tusenskille; «12.01.2026» (dato) er ikke et tall.
            if (!IsThousandsGrouped(text, commas > 1 ? ',' : '.'))
            {
                return null;
            }

            text = text.Replace(",", string.Empty).Replace(".", string.Empty);
        }
        else if (dots == 1 && IsThousandsGrouped(text, '.'))
        {
            // «10.000» er ti tusen, men «0.500» og «1234.567» er desimaltall.
            text = text.Replace(".", string.Empty);
        }
        else
        {
            text = text.Replace(',', '.');
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>«1.234.567»: første gruppe 1–3 sifre uten ledende null, deretter grupper på nøyaktig tre sifre.</summary>
    private static bool IsThousandsGrouped(string text, char separator)
    {
        var parts = (text.StartsWith('-') ? text[1..] : text).Split(separator);
        return parts.Length > 1
            && parts[0].Length is >= 1 and <= 3
            && parts[0][0] != '0'
            && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))
            && parts.Skip(1).All(p => p.Length == 3);
    }

    /// <summary>
    /// Google Sheets eksporterer pivottabeller med relasjoner uten <c>Id</c>/<c>Target</c>, som
    /// OpenXML-biblioteket avviser. Vi trenger ikke pivotene, så slike relasjoner fjernes før lesing.
    /// </summary>
    internal static MemoryStream RemoveInvalidRelationships(Stream xlsx)
    {
        var output = new MemoryStream();
        try
        {
            CopySanitized(xlsx, output);
        }
        catch
        {
            output.Dispose();
            throw;
        }

        output.Position = 0;
        return output;
    }

    private static void CopySanitized(Stream xlsx, MemoryStream output)
    {
        using (var source = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (source.Entries.Count > MaxZipEntries)
            {
                throw new InvalidDataException(TooLargeMessage);
            }

            // Telles mens vi pakker ut. Størrelsen i ZIP-hodet kan ikke stoles på.
            var remaining = MaxUncompressedBytes;
            foreach (var entry in source.Entries)
            {
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                using var input = entry.Open();
                using var dest = copy.Open();

                if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                {
                    remaining = CopyLimited(input, dest, remaining);
                    continue;
                }

                using var rels = new MemoryStream();
                remaining = CopyLimited(input, rels, remaining);
                rels.Position = 0;
                var doc = XDocument.Load(rels);
                doc.Root?.Elements()
                    .Where(e => e.Name.LocalName == "Relationship"
                                && (e.Attribute("Id") is null || e.Attribute("Target") is null))
                    .Remove();
                doc.Save(dest, System.Xml.Linq.SaveOptions.DisableFormatting);
            }
        }
    }

    private const string TooLargeMessage = "Regnearket er for stort til å importeres.";

    private static long CopyLimited(Stream input, Stream output, long remaining)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            remaining -= read;
            if (remaining < 0)
            {
                throw new InvalidDataException(TooLargeMessage);
            }

            output.Write(buffer, 0, read);
        }

        return remaining;
    }

    private static string Normalize(string value) => value.Trim().ToLower(CultureInfo.GetCultureInfo("nb-NO"));
}

/// <summary>Lager en formatert .xlsx med oversikt, prosjektfordeling og kontrolliste.</summary>
public static class BudgetWorkbookExporter
{
    private const string AmountFormat = "#,##0.00;-#,##0.00";

    public static byte[] Export(BudgetComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        using var workbook = new XLWorkbook();

        WriteOverview(workbook.AddWorksheet("Budsjettoversikt"), comparison);
        WriteProjects(workbook.AddWorksheet("Prosjekter"), comparison);
        WriteControl(workbook.AddWorksheet("Kontroll"), comparison);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteOverview(IXLWorksheet ws, BudgetComparison comparison)
    {
        ws.Cell(1, 1).Value = "Budsjettpost";
        var col = 2;
        foreach (var year in comparison.Years)
        {
            ws.Cell(1, col).Value = $"Budsjett {year}";
            ws.Cell(1, col + 1).Value = $"Regnskap {year}";
            ws.Cell(1, col + 2).Value = $"Avvik {year}";
            col += 3;
        }

        var row = 2;
        foreach (var line in comparison.Rows)
        {
            ws.Cell(row, 1).Value = line.Name;
            col = 2;
            foreach (var year in comparison.Years)
            {
                if (line.ByYear.GetValueOrDefault(year) is { } report)
                {
                    ws.Cell(row, col).Value = report.Budget;
                    ws.Cell(row, col + 1).Value = report.Actual;
                    ws.Cell(row, col + 2).FormulaA1 =
                        $"{ws.Cell(row, col + 1).Address.ToStringRelative()}-{ws.Cell(row, col).Address.ToStringRelative()}";
                }

                col += 3;
            }

            row++;
        }

        var lastDataRow = row - 1;
        ws.Cell(row, 1).Value = "Resultat";
        for (var c = 2; c < 2 + comparison.Years.Count * 3; c++)
        {
            var letter = ws.Column(c).ColumnLetter();
            ws.Cell(row, c).FormulaA1 = lastDataRow >= 2 ? $"SUM({letter}2:{letter}{lastDataRow})" : "0";
        }

        var lastCol = 1 + comparison.Years.Count * 3;
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(row).Style.Font.Bold = true;
        ws.Range(row, 1, row, lastCol).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        ws.Range(2, 2, row, lastCol).Style.NumberFormat.Format = AmountFormat;
        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);
        ws.Columns(1, lastCol).AdjustToContents();
    }

    private static void WriteProjects(IXLWorksheet ws, BudgetComparison comparison)
    {
        string[] headers = ["År", "Budsjettpost", "Prosjekt", "Regnskap"];
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
        }

        var row = 2;
        foreach (var year in comparison.Years)
        {
            var report = comparison.Reports[year];
            foreach (var line in report.Lines)
            {
                if (line.IsManualActual)
                {
                    ws.Cell(row, 1).Value = year;
                    ws.Cell(row, 2).Value = line.Name;
                    ws.Cell(row, 3).Value = "(manuelt tall)";
                    ws.Cell(row, 4).Value = line.Actual;
                    row++;
                    continue;
                }

                foreach (var project in line.Projects)
                {
                    ws.Cell(row, 1).Value = year;
                    ws.Cell(row, 2).Value = line.Name;
                    ws.Cell(row, 3).Value = project.Name;
                    ws.Cell(row, 4).Value = project.Amount;
                    row++;
                }
            }
        }

        ws.Row(1).Style.Font.Bold = true;
        ws.Column(4).Style.NumberFormat.Format = AmountFormat;
        if (row > 2)
        {
            ws.Range(1, 1, row - 1, 4).SetAutoFilter();
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns(1, 4).AdjustToContents();
    }

    private static void WriteControl(IXLWorksheet ws, BudgetComparison comparison)
    {
        var row = 1;
        foreach (var year in comparison.Years)
        {
            var report = comparison.Reports[year];
            ws.Cell(row, 1).Value = $"Kontroll {year}";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 13;
            row++;

            if (!report.HasFikenData)
            {
                ws.Cell(row++, 1).Value = "Ingen Fiken-data hentet for dette året.";
                row++;
                continue;
            }

            row = WriteKeyValue(ws, row, "Resultat i Fiken (resultatkonti 3000–8799)", report.FikenResult);
            row = WriteKeyValue(ws, row, "Fordelt på budsjettposter (uten manuelle tall)",
                report.Lines.Where(l => !l.IsManualActual).Sum(l => l.Actual));
            row = WriteKeyValue(ws, row, "Ufordelte posteringer (uten prosjekt)", report.UnallocatedTotal);
            row = WriteKeyValue(ws, row, "Prosjekter uten budsjettpost", report.UnlinkedTotal);
            row++;

            if (report.ProjectsLinkedToSeveralLines.Count > 0)
            {
                ws.Cell(row++, 1).Value = "Prosjekter koblet til flere poster (telles flere ganger): "
                    + string.Join(", ", report.ProjectsLinkedToSeveralLines.Select(p => p.Name));
            }

            if (report.PostingsWithSeveralProjects > 0)
            {
                ws.Cell(row++, 1).Value = $"{report.PostingsWithSeveralProjects} posteringslinje(r) har flere prosjekter. Bare det første prosjektet er brukt.";
            }

            if (report.ProjectsLinkedToSeveralLines.Count > 0 || report.PostingsWithSeveralProjects > 0)
            {
                row++;
            }

            if (report.UnlinkedProjects.Count > 0)
            {
                ws.Cell(row, 1).Value = "Prosjekter uten budsjettpost";
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;
                foreach (var project in report.UnlinkedProjects)
                {
                    ws.Cell(row, 1).Value = project.Name;
                    ws.Cell(row, 6).Value = project.Amount;
                    ws.Cell(row, 6).Style.NumberFormat.Format = AmountFormat;
                    row++;
                }

                row++;
            }

            if (report.UnallocatedPostings.Count > 0)
            {
                ws.Cell(row, 1).Value = "Ufordelte posteringer";
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;
                string[] headers = ["Bilagsnr", "Dato", "Konto", "Beskrivelse", "", "Beløp"];
                for (var i = 0; i < headers.Length; i++)
                {
                    ws.Cell(row, i + 1).Value = headers[i];
                }

                ws.Row(row).Style.Font.Italic = true;
                row++;
                foreach (var posting in report.UnallocatedPostings)
                {
                    if (posting.JournalEntryNumber is { } number)
                    {
                        ws.Cell(row, 1).Value = number;
                    }

                    ws.Cell(row, 2).Value = posting.Date ?? string.Empty;
                    ws.Cell(row, 3).Value = posting.Account;
                    ws.Cell(row, 4).Value = posting.Description ?? string.Empty;
                    ws.Cell(row, 6).Value = posting.Amount;
                    ws.Cell(row, 6).Style.NumberFormat.Format = AmountFormat;
                    row++;
                }
            }

            row += 2;
        }

        ws.Column(1).Width = 44;
        ws.Columns(2, 4).AdjustToContents();
        ws.Column(6).Width = 16;
    }

    private static int WriteKeyValue(IXLWorksheet ws, int row, string label, decimal? value)
    {
        ws.Cell(row, 1).Value = label;
        if (value.HasValue)
        {
            ws.Cell(row, 6).Value = value.Value;
            ws.Cell(row, 6).Style.NumberFormat.Format = AmountFormat;
        }

        return row + 1;
    }
}
