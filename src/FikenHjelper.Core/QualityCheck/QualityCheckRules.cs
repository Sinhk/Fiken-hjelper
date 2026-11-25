using System.Globalization;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.QualityCheck;

/// <summary>
/// Ren logikk for kvalitetssjekken: gjør åpne fakturaer, salg og kjøp fra Fiken om til funn.
/// Ingen HTTP, så alt kan testes direkte.
/// </summary>
public static class QualityCheckRules
{
    private const string InvoiceSaleKind = "invoice";

    private static readonly StringComparer NorwegianComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true);

    /// <summary>
    /// Ubetalte kundefakturaer. Utestående beløp hentes fra salget som hører til fakturaen: innebygd
    /// <c>sale</c>, eller et salg i <paramref name="sales"/> med samme KID når <c>sale</c> mangler.
    /// </summary>
    public static QualityCheckSection BuildCustomerInvoiceSection(
        IEnumerable<FikenInvoice> invoices,
        DateOnly referenceDate,
        IEnumerable<FikenSale>? sales = null)
    {
        var findings = new List<QualityFinding>();
        foreach (var (invoice, sale) in MatchInvoicesToSales(invoices, sales ?? []))
        {
            if (sale is not null && (sale.Deleted || sale.WriteOff || sale.Settled))
            {
                continue;
            }

            var date = ParseDate(invoice.IssueDate);
            if (date > referenceDate)
            {
                continue;
            }

            long? outstanding;
            string? note = null;
            if (sale?.OutstandingBalance is { } balance)
            {
                outstanding = balance;
            }
            else
            {
                // Uten salg vet vi ikke om fakturaen er delbetalt: bruttobeløpet er høyeste mulige beløp.
                outstanding = invoice.GrossInNok ?? invoice.Gross;
                note = "Fant ikke salget i Fiken; viser fakturabeløpet uten eventuelle delbetalinger.";
            }

            if (outstanding == 0)
            {
                continue;
            }

            var reference = invoice.InvoiceNumber is { } number
                ? $"Faktura {number.ToString(CultureInfo.InvariantCulture)}"
                : $"Faktura (id {invoice.InvoiceId.ToString(CultureInfo.InvariantCulture)})";

            findings.Add(CreateFinding(
                reference,
                invoice.Customer?.Name,
                date,
                ParseDate(invoice.DueDate),
                outstanding,
                invoice.Projects,
                referenceDate,
                invoice.InvoiceId,
                note));
        }

        return new QualityCheckSection(
            QualitySectionKeys.CustomerInvoices,
            "Ubetalte kundefakturaer",
            Sort(findings));
    }

    /// <summary>
    /// Åpne salg som ikke hører til en faktura i <paramref name="invoices"/> (typisk «annet salg»).
    /// Fakturasalg uten treff tas med, men merkes for manuell sjekk.
    /// </summary>
    public static QualityCheckSection BuildOtherSalesSection(
        IEnumerable<FikenSale> sales,
        IEnumerable<FikenInvoice> invoices,
        DateOnly referenceDate)
    {
        var saleList = sales as IReadOnlyCollection<FikenSale> ?? sales.ToList();
        var coveredSaleIds = MatchInvoicesToSales(invoices, saleList)
            .Where(m => m.Sale is not null)
            .Select(m => m.Sale!.SaleId)
            .ToHashSet();

        var findings = new List<QualityFinding>();
        foreach (var sale in saleList)
        {
            if (sale.Deleted || sale.WriteOff || sale.Settled || coveredSaleIds.Contains(sale.SaleId))
            {
                continue;
            }

            var date = ParseDate(sale.Date);
            if (date > referenceDate)
            {
                continue;
            }

            // Bare outstandingBalance brukes: netAmount/vatAmount er i salgsvaluta og kan ikke blandes med totalPaid i NOK.
            var outstanding = sale.OutstandingBalance;
            if (outstanding == 0)
            {
                continue;
            }

            string? note = null;
            var status = (QualityStatus?)null;
            if (string.Equals(sale.Kind, InvoiceSaleKind, StringComparison.OrdinalIgnoreCase))
            {
                note = "Fakturasalg uten treff blant ubetalte fakturaer. Sjekk i Fiken at det ikke telles to ganger.";
                status = QualityStatus.CheckManually;
            }
            else if (outstanding is null)
            {
                note = "Fiken oppga ikke utestående beløp.";
            }

            var reference = string.IsNullOrWhiteSpace(sale.SaleNumber)
                ? $"Salg (id {sale.SaleId.ToString(CultureInfo.InvariantCulture)})"
                : $"Salg {sale.SaleNumber}";

            findings.Add(CreateFinding(
                reference,
                sale.Customer?.Name,
                date,
                ParseDate(sale.DueDate),
                outstanding,
                sale.Projects,
                referenceDate,
                sale.SaleId,
                note,
                status));
        }

        return new QualityCheckSection(
            QualitySectionKeys.OtherSales,
            "Åpne salg (annet salg)",
            Sort(findings));
    }

    public static QualityCheckSection BuildSupplierPurchaseSection(
        IEnumerable<FikenPurchase> purchases,
        DateOnly referenceDate)
    {
        var findings = new List<QualityFinding>();
        foreach (var purchase in purchases)
        {
            if (purchase.Deleted || purchase.Paid || purchase.Settled)
            {
                continue;
            }

            var date = ParseDate(purchase.Date);
            if (date > referenceDate)
            {
                continue;
            }

            var outstanding = PurchaseOutstandingInNok(purchase);
            if (outstanding == 0)
            {
                continue;
            }

            var reference = string.IsNullOrWhiteSpace(purchase.Identifier)
                ? $"Kjøp (id {purchase.PurchaseId.ToString(CultureInfo.InvariantCulture)})"
                : $"Kjøp {purchase.Identifier}";

            findings.Add(CreateFinding(
                reference,
                purchase.Supplier?.Name,
                date,
                ParseDate(purchase.DueDate),
                outstanding,
                purchase.Projects,
                referenceDate,
                purchase.PurchaseId,
                outstanding is null ? "Beløp i fremmed valuta eller mangler; sjekk i Fiken." : null));
        }

        return new QualityCheckSection(
            QualitySectionKeys.SupplierPurchases,
            "Ubetalte kjøp",
            Sort(findings));
    }

    /// <summary>
    /// Brutto minus betalinger, i øre NOK. <c>null</c> hvis et linje- eller betalingsbeløp bare finnes i fremmed valuta.
    /// </summary>
    public static long? PurchaseOutstandingInNok(FikenPurchase purchase)
    {
        long gross = 0;
        foreach (var line in purchase.Lines ?? [])
        {
            if (line.NetPrice is null && (line.NetPriceInCurrency ?? 0) != 0)
            {
                return null;
            }

            gross += (line.NetPrice ?? 0) + (line.Vat ?? 0);
        }

        long paid = 0;
        foreach (var payment in purchase.Payments ?? [])
        {
            if (payment.AmountInNok is { } nok)
            {
                paid += nok;
            }
            else if (IsNok(payment.Currency))
            {
                paid += payment.Amount ?? 0;
            }
            else
            {
                return null;
            }
        }

        return gross - paid;
    }

    /// <summary>
    /// Antall dager over forfall per <paramref name="referenceDate"/>. Forfall samme dag regnes ikke som forfalt.
    /// </summary>
    public static int? DaysOverdue(DateOnly? dueDate, DateOnly referenceDate)
    {
        if (dueDate is not { } due)
        {
            return null;
        }

        var days = referenceDate.DayNumber - due.DayNumber;
        return days > 0 ? days : null;
    }

    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Fiken bruker YYYY-MM-DD, men tåler også tidsstempel ved å se på de første ti tegnene.
        var span = value.AsSpan().Trim();
        if (span.Length > 10)
        {
            span = span[..10];
        }

        return DateOnly.TryParseExact(span, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Kobler hver faktura til salget sitt: innebygd <c>sale</c> først (beriket med fullt salg fra listen hvis det finnes),
    /// ellers et unikt fakturasalg med samme KID.
    /// </summary>
    private static List<(FikenInvoice Invoice, FikenSale? Sale)> MatchInvoicesToSales(
        IEnumerable<FikenInvoice> invoices,
        IEnumerable<FikenSale> sales)
    {
        var saleList = sales.ToList();
        var salesById = saleList
            .GroupBy(s => s.SaleId)
            .ToDictionary(g => g.Key, g => g.First());
        var invoiceSalesByKid = saleList
            .Where(s => !string.IsNullOrWhiteSpace(s.Kid)
                && string.Equals(s.Kind, InvoiceSaleKind, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Kid!.Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

        var usedSaleIds = new HashSet<long>();
        var result = new List<(FikenInvoice, FikenSale?)>();
        foreach (var invoice in invoices)
        {
            FikenSale? sale = invoice.Sale;
            if (sale is not null && salesById.TryGetValue(sale.SaleId, out var fullSale))
            {
                sale = fullSale;
            }
            else if (sale is null
                && !string.IsNullOrWhiteSpace(invoice.Kid)
                && invoiceSalesByKid.TryGetValue(invoice.Kid.Trim(), out var kidSale)
                && !usedSaleIds.Contains(kidSale.SaleId))
            {
                sale = kidSale;
            }

            if (sale is not null)
            {
                usedSaleIds.Add(sale.SaleId);
            }

            result.Add((invoice, sale));
        }

        return result;
    }

    private static bool IsNok(string? currency) =>
        string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "NOK", StringComparison.OrdinalIgnoreCase);

    private static QualityFinding CreateFinding(
        string reference,
        string? counterparty,
        DateOnly? date,
        DateOnly? dueDate,
        long? outstanding,
        IReadOnlyList<FikenProjectRef>? projects,
        DateOnly referenceDate,
        long fikenId,
        string? note = null,
        QualityStatus? statusOverride = null)
    {
        var daysOverdue = outstanding > 0 ? DaysOverdue(dueDate, referenceDate) : null;
        var status = statusOverride ?? outstanding switch
        {
            null => QualityStatus.CheckManually,
            < 0 => QualityStatus.Credit,
            _ => daysOverdue is null ? QualityStatus.Open : QualityStatus.Overdue
        };

        if (status == QualityStatus.Credit && note is null)
        {
            note = "Negativ saldo (overbetaling eller kreditnota som ikke er motregnet).";
        }

        return new QualityFinding(
            reference,
            string.IsNullOrWhiteSpace(counterparty) ? "(ukjent)" : counterparty.Trim(),
            date,
            dueDate,
            daysOverdue,
            outstanding,
            FormatProjects(projects),
            status,
            fikenId,
            note);
    }

    private static string? FormatProjects(IReadOnlyList<FikenProjectRef>? projects)
    {
        if (projects is null || projects.Count == 0)
        {
            return null;
        }

        var names = projects
            .Select(p => string.IsNullOrWhiteSpace(p.Name)
                ? p.Number ?? p.ProjectId.ToString(CultureInfo.InvariantCulture)
                : p.Name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return string.Join(", ", names);
    }

    /// <summary>Forfalte først (eldst forfall øverst), så poster som må sjekkes, kreditsaldoer og åpne.</summary>
    private static IReadOnlyList<QualityFinding> Sort(List<QualityFinding> findings) =>
        findings
            .OrderByDescending(f => f.Status)
            .ThenBy(f => f.DueDate ?? DateOnly.MaxValue)
            .ThenBy(f => f.Counterparty, NorwegianComparer)
            .ThenBy(f => f.Reference, StringComparer.Ordinal)
            .ToList();
}
