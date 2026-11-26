using System.Text;
using FikenHjelper.Core.QualityCheck;

namespace FikenHjelper.Core.Grants;

/// <summary>
/// Lager tekst av tilskudd som kan limes rett inn i Word, Google Docs eller et regneark, på samme måte som
/// <see cref="QualityCheckTextFormatter"/>: HTML med ekte tabeller, tabulatorseparert tekst (tall uten tusenskille,
/// <c>1234,50</c>) og Markdown (tall med tusenskille, <c>1 234,50</c>). Manglende verdier skrives som tekst, aldri som 0.
/// </summary>
public static class GrantTextFormatter
{
    public const string NotRegistered = "ikke registrert";
    public const string MissingFikenData = "mangler Fiken-data";
    private const string Unknown = "ukjent";
    private const string NotSet = "ikke satt";
    private const string Dash = "–";

    // Dokumentet bygges én gang per format, fordi bare beløpene formateres ulikt.
    private abstract record Block;

    private sealed record Heading(int Level, string Text) : Block;

    private sealed record Paragraph(string Text) : Block;

    private sealed record Table(string[] Headers, bool[] Numeric, List<string[]> Rows, bool HasTotalRow) : Block;

    // ---- Ett tilskudd ----

    public static string ToTabSeparated(GrantReport report, bool includeOtherIncome = true) =>
        RenderTabs(BuildGrant(report, includeOtherIncome, QualityCheckTextFormatter.FormatKronerPlain));

    public static string ToHtml(GrantReport report, bool includeOtherIncome = true) =>
        RenderHtml(BuildGrant(report, includeOtherIncome, QualityCheckTextFormatter.FormatKroner));

    public static string ToMarkdown(GrantReport report, bool includeOtherIncome = true) =>
        RenderMarkdown(BuildGrant(report, includeOtherIncome, QualityCheckTextFormatter.FormatKroner));

    // ---- Oversikt ----

    /// <param name="rows">De synlige tilskuddene (etter filter).</param>
    /// <param name="filterText">Beskrivelse av filteret, f.eks. «År 2027, status Innvilget». Null eller tom når alt vises.</param>
    public static string OverviewToTabSeparated(IReadOnlyList<GrantReport> rows, string? filterText = null) =>
        RenderTabs(BuildOverview(rows, filterText, QualityCheckTextFormatter.FormatKronerPlain));

    public static string OverviewToHtml(IReadOnlyList<GrantReport> rows, string? filterText = null) =>
        RenderHtml(BuildOverview(rows, filterText, QualityCheckTextFormatter.FormatKroner));

    public static string OverviewToMarkdown(IReadOnlyList<GrantReport> rows, string? filterText = null) =>
        RenderMarkdown(BuildOverview(rows, filterText, QualityCheckTextFormatter.FormatKroner));

    // ---- Felles tekster for manglende verdier (brukes også av arbeidsboken) ----

    /// <summary>Hvorfor «brukt» mangler, eller null når den har verdi.</summary>
    public static string? UsedMissingText(GrantReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.UsedIsUnregistered ? NotRegistered : report.UsedNeedsFikenData ? MissingFikenData : null;
    }

    public const string PaymentNotFound = "utbetaling ikke funnet i Fiken";

    /// <summary>
    /// Hvorfor «mottatt» ikke kan regnes ut, eller null når den har verdi. Skiller mellom manglende Fiken-data og
    /// en koblet utbetaling som ikke finnes i Fiken-dataene.
    /// </summary>
    public static string? ReceivedMissingText(GrantReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (GrantOverviewLogic.ReceivedOrNull(report) is not null)
        {
            return null;
        }

        return report.HasFikenData ? PaymentNotFound : MissingFikenData;
    }

    // ---- Bygging ----

    private static List<Block> BuildGrant(GrantReport report, bool includeOtherIncome, Func<long, string> format)
    {
        ArgumentNullException.ThrowIfNull(report);
        var grant = report.Grant;
        string Money(decimal value) => format(ToOre(value));

        var blocks = new List<Block> { new Heading(2, $"Tilskudd: {grant.DisplayName}") };

        var period = (grant.UseFrom, grant.UseTo) switch
        {
            (null, null) => NotSet,
            ({ } from, { } to) => $"{QualityCheckTextFormatter.FormatDate(from)} – {QualityCheckTextFormatter.FormatDate(to)}",
            ({ } from, null) => $"fra {QualityCheckTextFormatter.FormatDate(from)}",
            (null, { } to) => $"til {QualityCheckTextFormatter.FormatDate(to)}"
        };

        var received = GrantOverviewLogic.ReceivedOrNull(report);
        var remaining = GrantOverviewLogic.RemainingOrNull(report);
        var unused = received is null ? null : report.Unused;

        var summary = new Table(["Felt", "Verdi"], [false, true], [], false);
        void Row(string label, string value) => summary.Rows.Add([label, value]);
        Row("Tilskuddsgiver", string.IsNullOrWhiteSpace(grant.Grantor) ? NotSet : grant.Grantor.Trim());
        if (!string.IsNullOrWhiteSpace(grant.Scheme))
        {
            Row("Ordning", grant.Scheme.Trim());
        }

        Row("Bruksperiode", period);
        Row("Innvilget (kr)", report.Granted is { } granted ? Money(granted) : NotSet);
        Row("Mottatt (kr)", received is { } r ? Money(r) : ReceivedMissingText(report) ?? Unknown);
        Row("Brukt (kr)", report.Used is { } used ? Money(used) : UsedMissingText(report) ?? Unknown);
        Row("Gjenstår å motta (kr)", remaining is { } rem ? Money(rem) : Unknown);
        Row("Ubrukt (mottatt − brukt) (kr)", unused is { } un ? Money(un) : Unknown);
        blocks.Add(new Heading(3, "Sammendrag"));
        blocks.Add(summary);

        blocks.Add(new Heading(3, "Bruk per konto"));
        if (UsedMissingText(report) is not null && report.UsedByAccount.Count == 0 && report.ManualUsed == 0m)
        {
            blocks.Add(new Paragraph(report.UsedNeedsFikenData
                ? "Mangler Fiken-data, så bruken kan ikke vises."
                : "Bruk er ikke registrert."));
        }
        else if (report.UsedByAccount.Count == 0 && report.ManualUsed == 0m)
        {
            blocks.Add(new Paragraph("Ingen kostnader funnet i bruksperioden."));
        }
        else
        {
            blocks.Add(AccountTable(report.UsedByAccount, report.ManualUsed, report.Used, Money));
        }

        if (includeOtherIncome && report.OtherIncomeByAccount.Count > 0)
        {
            blocks.Add(new Heading(3, "Andre inntekter på prosjektet"));
            blocks.Add(AccountTable(report.OtherIncomeByAccount, 0m, report.OtherIncome, Money));
        }

        return blocks;
    }

    private static Table AccountTable(IReadOnlyList<GrantAccountSum> accounts, decimal manual, decimal? total, Func<decimal, string> money)
    {
        var table = new Table(["Konto", "Navn", "Beløp (kr)"], [false, false, true], [], true);
        foreach (var account in accounts)
        {
            table.Rows.Add([account.Account.ToString(System.Globalization.CultureInfo.InvariantCulture), account.AccountName ?? string.Empty, money(account.Amount)]);
        }

        if (manual != 0m)
        {
            table.Rows.Add(["", "Manuell bruk", money(manual)]);
        }

        table.Rows.Add(["Sum", "", total is { } t ? money(t) : MissingFikenData]);
        return table;
    }

    private static List<Block> BuildOverview(IReadOnlyList<GrantReport> rows, string? filterText, Func<long, string> format)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string Money(decimal value) => format(ToOre(value));
        string Opt(decimal? value) => value is { } v ? Money(v) : Dash;

        var blocks = new List<Block> { new Heading(2, "Tilskudd") };
        if (!string.IsNullOrWhiteSpace(filterText))
        {
            blocks.Add(new Paragraph($"Utvalg: {filterText.Trim()}"));
        }

        if (rows.Count == 0)
        {
            blocks.Add(new Paragraph("Ingen tilskudd passer utvalget."));
            return blocks;
        }

        var table = new Table(
            ["Navn", "Tilskuddsgiver", "Type", "Status", "Søkt (kr)", "Innvilget (kr)", "Mottatt (kr)", "Brukt (kr)", "Gjenstår å motta (kr)"],
            [false, false, false, false, true, true, true, true, true],
            [],
            true);
        foreach (var r in rows)
        {
            table.Rows.Add(
            [
                r.Grant.DisplayName,
                r.Grant.Grantor ?? string.Empty,
                Grant.KindText(r.Grant.Kind),
                Grant.StatusText(r.Grant.Status),
                Opt(r.Grant.AppliedAmount),
                Opt(r.Granted),
                GrantOverviewLogic.ReceivedOrNull(r) is { } rec ? Money(rec) : ReceivedMissingText(r) ?? Unknown,
                UsedMissingText(r) ?? Opt(r.Used),
                Opt(GrantOverviewLogic.RemainingOrNull(r))
            ]);
        }

        var totals = GrantOverviewLogic.Totals(rows);
        string Total(GrantTotal t) => !t.HasValue ? Dash : Money(t.Sum) + (t.IsPartial ? " *" : string.Empty);
        table.Rows.Add([$"Sum ({rows.Count} tilskudd)", "", "", "", Total(totals.Applied), Total(totals.Granted), Total(totals.Received), Total(totals.Used), Total(totals.Remaining)]);
        blocks.Add(table);

        if (GrantOverviewLogic.PartialNotes(totals) is { Count: > 0 } notes)
        {
            blocks.Add(new Paragraph($"* Delsum, siden noen tilskudd mangler verdi: {string.Join("; ", notes)}."));
        }

        blocks.Add(new Paragraph("Brukt er brutto kostnader på de koblede prosjektene i bruksperioden, ganget med tilskuddets andel. Gjenstår å motta = innvilget − mottatt."));
        return blocks;
    }

    // Stjerne markerer delsum og må ikke bli kursiv eller punktliste i Markdown.
    private static string MarkdownText(string value) =>
        QualityCheckTextFormatter.CleanTab(value).Replace("\\", "\\\\").Replace("|", "\\|").Replace("*", "\\*");

    private static long ToOre(decimal kroner) => (long)Math.Round(kroner * 100m, MidpointRounding.AwayFromZero);

    // ---- Opptegning ----

    private static string RenderTabs(List<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading h:
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(QualityCheckTextFormatter.CleanTab(h.Text)).Append('\n');
                    break;
                case Paragraph p:
                    sb.Append(QualityCheckTextFormatter.CleanTab(p.Text)).Append('\n');
                    break;
                case Table t:
                    sb.AppendJoin('\t', t.Headers).Append('\n');
                    foreach (var row in t.Rows)
                    {
                        sb.AppendJoin('\t', row.Select(QualityCheckTextFormatter.CleanTab)).Append('\n');
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    private static string RenderHtml(List<Block> blocks)
    {
        const string Cell = "border:1px solid #999;padding:2px 6px;";
        const string NumCell = Cell + "text-align:right;";
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading h:
                    sb.Append("<h").Append(h.Level).Append('>').Append(QualityCheckTextFormatter.Encode(h.Text))
                        .Append("</h").Append(h.Level).Append(">\n");
                    break;
                case Paragraph p:
                    sb.Append("<p>").Append(QualityCheckTextFormatter.Encode(p.Text)).Append("</p>\n");
                    break;
                case Table t:
                    sb.Append("<table style=\"border-collapse:collapse\"><thead><tr>");
                    for (var i = 0; i < t.Headers.Length; i++)
                    {
                        sb.Append("<th style=\"").Append(t.Numeric[i] ? NumCell : Cell).Append("\">")
                            .Append(QualityCheckTextFormatter.Encode(t.Headers[i])).Append("</th>");
                    }

                    sb.Append("</tr></thead><tbody>\n");
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        var bold = t.HasTotalRow && r == t.Rows.Count - 1;
                        sb.Append("<tr>");
                        for (var i = 0; i < t.Rows[r].Length; i++)
                        {
                            var text = QualityCheckTextFormatter.Encode(t.Rows[r][i]);
                            sb.Append("<td style=\"").Append(t.Numeric[i] ? NumCell : Cell).Append("\">")
                                .Append(bold && text.Length > 0 ? "<b>" + text + "</b>" : text).Append("</td>");
                        }

                        sb.Append("</tr>\n");
                    }

                    sb.Append("</tbody></table>\n");
                    break;
            }
        }

        return sb.ToString();
    }

    private static string RenderMarkdown(List<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading h:
                    if (sb.Length > 0)
                    {
                        sb.Append('\n');
                    }

                    sb.Append(new string('#', h.Level)).Append(' ').Append(MarkdownText(h.Text)).Append("\n\n");
                    break;
                case Paragraph p:
                    sb.Append(MarkdownText(p.Text)).Append("\n\n");
                    break;
                case Table t:
                    sb.Append("| ").AppendJoin(" | ", t.Headers).Append(" |\n");
                    sb.Append('|').Append(string.Concat(t.Numeric.Select(n => n ? "---:|" : "---|"))).Append('\n');
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        var bold = t.HasTotalRow && r == t.Rows.Count - 1;
                        var cells = t.Rows[r].Select(MarkdownText)
                            .Select(c => bold && c.Length > 0 ? "**" + c + "**" : c);
                        sb.Append("| ").AppendJoin(" | ", cells).Append(" |\n");
                    }

                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
