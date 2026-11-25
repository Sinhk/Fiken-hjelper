using System.ComponentModel;
using FikenHjelper.Core.Analysis;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Mcp.Services;
using ModelContextProtocol.Server;

namespace FikenHjelper.Mcp.Tools;

[McpServerToolType]
public sealed class AnomalyTools(
    BudgetReportService reports,
    FikenContextService context,
    FikenLookups lookups,
    IFikenPurchasingClient purchasing,
    TimeProvider time)
{
    public const int StaleInboxDays = 14;
    public const int StaleDraftDays = 30;

    [McpServerTool(Name = "find_accounting_anomalies", Title = "Finn avvik og glemte oppgaver", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Kontrollerer regnskapet for et år: posteringer uten prosjekt, prosjekter som ikke er koblet til budsjettet (eller er koblet flere ganger), forfalte ubetalte kjøp og salg, kjøp uten vedlegg, gamle ubehandlede bilag i innboks/EHF, gamle kjøpsutkast og posteringer etter prosjektets sluttdato.")]
    public Task<object> FindAccountingAnomalies(
        [Description("År. Standard: inneværende år.")] int? year = null,
        [Description("Hvilke sjekker som skal kjøres (standard alle): ufordelte_posteringer, ukoblede_prosjekter, ugyldig_fordeling, forfalte_kjop, forfalte_salg, kjop_uten_vedlegg, gammel_innboks, gamle_kjopsutkast, postering_etter_prosjektslutt.")] string[]? checks = null,
        [Description("Maks antall funn per type (1-200).")] int maxPerType = 25,
        CancellationToken ct = default) => ToolSupport.Run<object>(async () =>
    {
        var today = ToolSupport.Today(time);
        var y = ToolSupport.ValidateYear(year ?? today.Year);
        var selected = (checks is { Length: > 0 } ? checks : AnomalyTypes.All)
            .Select(c => c.Trim().ToLowerInvariant())
            .ToHashSet();
        var unknown = selected.Except(AnomalyTypes.All).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Ukjente sjekker: {string.Join(", ", unknown)}.");
        }

        var slug = await context.GetCompanySlugAsync(ct);
        var anomalies = new List<Anomaly>();

        if (selected.Overlaps([AnomalyTypes.UnallocatedPostings, AnomalyTypes.UnlinkedProjects, AnomalyTypes.InvalidProjectSplits]))
        {
            var comparison = await reports.BuildComparisonAsync([y], refresh: false, ct);
            anomalies.AddRange(AnomalyFinder.FromBudgetReport(comparison.Reports[y]).Where(a => selected.Contains(a.Type)));
        }

        if (selected.Contains(AnomalyTypes.OverduePurchases))
        {
            anomalies.AddRange(AnomalyFinder.OverduePurchases(await purchasing.GetPurchasesAsync(slug, paid: false, ct: ct), today));
        }

        if (selected.Contains(AnomalyTypes.OverdueSales))
        {
            anomalies.AddRange(AnomalyFinder.OverdueSales(await purchasing.GetSalesAsync(slug, settled: false, ct: ct), today));
        }

        if (selected.Contains(AnomalyTypes.PurchasesWithoutAttachment))
        {
            var purchases = await purchasing.GetPurchasesAsync(slug, dateFrom: new DateOnly(y, 1, 1), dateTo: new DateOnly(y, 12, 31), ct: ct);
            anomalies.AddRange(AnomalyFinder.PurchasesWithoutAttachment(purchases));
        }

        if (selected.Contains(AnomalyTypes.StaleInbox))
        {
            var inbox = await purchasing.GetInboxDocumentsAsync(slug, "unused", ct: ct);
            var ehf = await purchasing.GetEhfDocumentsAsync(slug, "unprocessed", ct: ct);
            anomalies.AddRange(AnomalyFinder.StaleInbox(inbox, ehf, today, StaleInboxDays));
        }

        if (selected.Contains(AnomalyTypes.StaleDrafts))
        {
            anomalies.AddRange(AnomalyFinder.StaleDrafts(await purchasing.GetPurchaseDraftsAsync(slug, ct), today, StaleDraftDays));
        }

        if (selected.Contains(AnomalyTypes.PostingsAfterProjectEnd))
        {
            var entries = await reports.GetJournalEntriesAsync(slug, y, refresh: false, ct);
            var projects = await lookups.GetProjectsAsync(slug, ct);
            anomalies.AddRange(AnomalyFinder.PostingsAfterProjectEnd(entries, projects));
        }

        var limit = Math.Clamp(maxPerType, 1, 200);
        var groups = anomalies.GroupBy(a => a.Type).ToList();
        return new
        {
            year = y,
            checks = selected.Order().ToList(),
            summary = groups.Select(g => new
            {
                type = g.Key,
                count = g.Count(),
                totalKr = g.Sum(a => a.AmountKr ?? 0),
                shown = Math.Min(g.Count(), limit)
            }).ToList(),
            anomalies = groups.SelectMany(g => g
                    .OrderBy(a => a.Severity == AnomalyFinder.High ? 0 : a.Severity == AnomalyFinder.Medium ? 1 : 2)
                    .ThenBy(a => a.Date, StringComparer.Ordinal)
                    .Take(limit))
                .ToList(),
            note = BudgetTools.SignNote
        };
    });
}
