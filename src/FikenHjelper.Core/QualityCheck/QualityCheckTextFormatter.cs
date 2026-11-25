using System.Globalization;
using System.Net;
using System.Text;

namespace FikenHjelper.Core.QualityCheck;

/// <summary>
/// Lager tekst av kvalitetssjekken som kan limes rett inn i Word, Google Docs eller et regneark.
/// Tabulatorseparert tekst blir tabell når den limes inn i et regneark; Markdown passer i chat og notater.
/// </summary>
public static class QualityCheckTextFormatter
{
    private static readonly string[] Columns =
        ["Status", "Referanse", "Motpart", "Dato", "Forfall", "Dager forfalt", "Utestående (kr)", "Prosjekt", "Merknad"];

    private static readonly NumberFormatInfo NorwegianNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = "\u00A0",
        NumberGroupSizes = [3],
        NegativeSign = "-"
    };

    private static readonly NumberFormatInfo SpreadsheetNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NegativeSign = "-"
    };

    /// <summary>Formaterer øre som kroner med norsk tallformat, f.eks. <c>123450</c> → <c>1 234,50</c> (hardt mellomrom).</summary>
    public static string FormatKroner(long ore) =>
        (ore / 100m).ToString("N2", NorwegianNumbers);

    /// <summary>
    /// Kroner uten tusenskille, f.eks. <c>1234,50</c>, så regneark med norsk språk leser verdien som tall.
    /// </summary>
    public static string FormatKronerPlain(long ore) =>
        (ore / 100m).ToString("F2", SpreadsheetNumbers);

    public static string FormatDate(DateOnly? date) =>
        date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";

    public static string StatusText(QualityStatus status) => status switch
    {
        QualityStatus.Overdue => "Forfalt",
        QualityStatus.CheckManually => "Sjekk",
        QualityStatus.Credit => "Kreditsaldo",
        QualityStatus.Open => "Åpen",
        _ => "OK"
    };

    public static string ToTabSeparated(QualityCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append("Kvalitetssjekk per ").Append(FormatDate(report.ReferenceDate)).Append('\n');

        foreach (var section in report.Sections)
        {
            sb.Append('\n').Append(SectionHeading(section)).Append('\n');
            if (section.Findings.Count == 0)
            {
                sb.Append("Ingen åpne poster.\n");
                continue;
            }

            sb.AppendJoin('\t', Columns).Append('\n');
            foreach (var row in section.Findings.Select(f => Cells(f, FormatKronerPlain)))
            {
                sb.AppendJoin('\t', row.Select(CleanTab)).Append('\n');
            }

            sb.Append("Sum\t\t\t\t\t\t").Append(FormatKronerPlain(section.TotalOre)).Append("\t\t\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Enkel HTML med én tabell per seksjon. Brukes på utklippstavlen sammen med <see cref="ToTabSeparated"/>,
    /// slik at innliming i Word og Google Docs gir en ekte tabell.
    /// </summary>
    public static string ToHtml(QualityCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        const string Cell = "border:1px solid #999;padding:2px 6px;";
        const string NumCell = Cell + "text-align:right;";
        var sb = new StringBuilder();
        sb.Append("<h2>Kvalitetssjekk per ").Append(FormatDate(report.ReferenceDate)).Append("</h2>\n");

        foreach (var section in report.Sections)
        {
            sb.Append("<h3>").Append(Encode(SectionHeading(section))).Append("</h3>\n");
            if (section.Findings.Count == 0)
            {
                sb.Append("<p>Ingen åpne poster.</p>\n");
                continue;
            }

            sb.Append("<table style=\"border-collapse:collapse\"><thead><tr>");
            foreach (var (column, i) in Columns.Select((c, i) => (c, i)))
            {
                sb.Append("<th style=\"").Append(IsNumberColumn(i) ? NumCell : Cell).Append("\">")
                    .Append(Encode(column)).Append("</th>");
            }

            sb.Append("</tr></thead><tbody>\n");
            foreach (var row in section.Findings.Select(f => Cells(f, FormatKroner)))
            {
                sb.Append("<tr>");
                for (var i = 0; i < row.Length; i++)
                {
                    sb.Append("<td style=\"").Append(IsNumberColumn(i) ? NumCell : Cell).Append("\">")
                        .Append(Encode(row[i])).Append("</td>");
                }

                sb.Append("</tr>\n");
            }

            sb.Append("<tr><td style=\"").Append(Cell).Append("\"><b>Sum</b></td>");
            for (var i = 1; i < Columns.Length; i++)
            {
                var value = i == AmountColumn ? "<b>" + Encode(FormatKroner(section.TotalOre)) + "</b>" : "";
                sb.Append("<td style=\"").Append(IsNumberColumn(i) ? NumCell : Cell).Append("\">").Append(value).Append("</td>");
            }

            sb.Append("</tr>\n</tbody></table>\n");
        }

        return sb.ToString();
    }

    public static string ToMarkdown(QualityCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.Append("## Kvalitetssjekk per ").Append(FormatDate(report.ReferenceDate)).Append("\n");

        foreach (var section in report.Sections)
        {
            sb.Append("\n### ").Append(SectionHeading(section)).Append("\n\n");
            if (section.Findings.Count == 0)
            {
                sb.Append("Ingen åpne poster.\n");
                continue;
            }

            sb.Append("| ").AppendJoin(" | ", Columns).Append(" |\n");
            sb.Append('|').Append(string.Concat(Columns.Select((_, i) => IsNumberColumn(i) ? "---:|" : "---|"))).Append('\n');
            foreach (var row in section.Findings.Select(f => Cells(f, FormatKroner)))
            {
                sb.Append("| ").AppendJoin(" | ", row.Select(EscapeMarkdown)).Append(" |\n");
            }

            sb.Append("| **Sum** |  |  |  |  |  | **").Append(FormatKroner(section.TotalOre)).Append("** |  |  |\n");
        }

        return sb.ToString();
    }

    private static string SectionHeading(QualityCheckSection section)
    {
        if (section.Findings.Count == 0)
        {
            return $"{section.Title}: ingen";
        }

        var text = $"{section.Title}: {section.Findings.Count} stk, {FormatKroner(section.TotalOre)} kr";
        if (section.OverdueCount > 0)
        {
            text += $" (forfalt: {section.OverdueCount} stk, {FormatKroner(section.OverdueOre)} kr)";
        }

        if (section.UnknownAmountCount > 0)
        {
            text += $"; {section.UnknownAmountCount} stk med ukjent beløp er ikke med i summen";
        }

        return text;
    }

    private static string[] Cells(QualityFinding f, Func<long, string> formatAmount) =>
    [
        StatusText(f.Status),
        f.Reference,
        f.Counterparty,
        FormatDate(f.Date),
        FormatDate(f.DueDate),
        f.DaysOverdue?.ToString(CultureInfo.InvariantCulture) ?? "",
        f.OutstandingOre is { } ore ? formatAmount(ore) : "ukjent",
        f.Project ?? "",
        f.Note ?? ""
    ];

    private const int DaysColumn = 5;
    private const int AmountColumn = 6;

    private static bool IsNumberColumn(int index) => index is DaysColumn or AmountColumn;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string CleanTab(string value) =>
        value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string EscapeMarkdown(string value) =>
        CleanTab(value).Replace("|", "\\|");
}
