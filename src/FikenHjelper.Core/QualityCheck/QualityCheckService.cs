using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.QualityCheck;

/// <summary>
/// Henter åpne poster fra Fiken (bare GET-kall) og bygger en kvalitetssjekk før styremøte eller årsoppgjør.
/// Laget for å kunne brukes både fra web-appen og fra MCP-serveren.
/// </summary>
public sealed class QualityCheckService(
    IFikenClient fiken,
    IFikenPurchasingClient purchasing,
    TimeProvider timeProvider)
{
    private static readonly TimeZoneInfo NorwegianTimeZone = ResolveNorwegianTimeZone();

    public async Task<QualityCheckReport> RunAsync(
        string companySlug,
        DateOnly? referenceDate = null,
        CancellationToken ct = default)
    {
        var fetchedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), NorwegianTimeZone);
        var refDate = referenceDate ?? DateOnly.FromDateTime(fetchedAt.DateTime);

        // Kallene går sekvensielt uansett (FikenRateLimitHandler), så de kjøres etter hverandre.
        var invoices = await fiken.GetUnsettledInvoicesAsync(companySlug, ct);
        var sales = await purchasing.GetSalesAsync(companySlug, settled: false, ct: ct);
        var purchases = await purchasing.GetPurchasesAsync(companySlug, paid: false, ct: ct);

        return Build(invoices, sales, purchases, refDate, fetchedAt);
    }

    public static QualityCheckReport Build(
        IReadOnlyList<FikenInvoice> invoices,
        IReadOnlyList<FikenSale> sales,
        IReadOnlyList<FikenPurchase> purchases,
        DateOnly referenceDate,
        DateTimeOffset fetchedAt)
    {
        var sections = new List<QualityCheckSection>
        {
            QualityCheckRules.BuildCustomerInvoiceSection(invoices, referenceDate, sales),
            QualityCheckRules.BuildOtherSalesSection(sales, invoices, referenceDate),
            QualityCheckRules.BuildSupplierPurchaseSection(purchases, referenceDate)
        };

        return new QualityCheckReport(referenceDate, fetchedAt, sections);
    }

    private static TimeZoneInfo ResolveNorwegianTimeZone()
    {
        // «Europe/Oslo» fungerer på Linux og på Windows med ICU (.NET 6+); Windows-ID-en er reserve.
        foreach (var id in new[] { "Europe/Oslo", "W. Europe Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Local;
    }
}
