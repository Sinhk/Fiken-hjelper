using System.Globalization;
using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Analysis;

public static class AnomalyTypes
{
    public const string UnallocatedPostings = "ufordelte_posteringer";
    public const string UnlinkedProjects = "ukoblede_prosjekter";
    public const string ProjectsLinkedToSeveralLines = "prosjekt_i_flere_poster";
    public const string OverduePurchases = "forfalte_kjop";
    public const string OverdueSales = "forfalte_salg";
    public const string PurchasesWithoutAttachment = "kjop_uten_vedlegg";
    public const string StaleInbox = "gammel_innboks";
    public const string StaleDrafts = "gamle_kjopsutkast";
    public const string PostingsAfterProjectEnd = "postering_etter_prosjektslutt";

    public static readonly IReadOnlyList<string> All =
    [
        UnallocatedPostings,
        UnlinkedProjects,
        ProjectsLinkedToSeveralLines,
        OverduePurchases,
        OverdueSales,
        PurchasesWithoutAttachment,
        StaleInbox,
        StaleDrafts,
        PostingsAfterProjectEnd
    ];
}

public sealed record Anomaly(
    string Type,
    string Severity,
    string? Date,
    string Description,
    decimal? AmountKr,
    string? Reference);

/// <summary>Rene sjekker for mulige feilføringer og glemte oppgaver. Fiken-data hentes av kalleren.</summary>
public static class AnomalyFinder
{
    public const string High = "høy";
    public const string Medium = "middels";
    public const string Low = "lav";

    public static IEnumerable<Anomaly> FromBudgetReport(BudgetYearReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var posting in report.UnallocatedPostings)
        {
            yield return new Anomaly(
                AnomalyTypes.UnallocatedPostings,
                Medium,
                posting.Date,
                $"Postering på konto {posting.Account} uten prosjekt: {posting.Description}",
                posting.Amount,
                posting.JournalEntryNumber is { } n ? $"bilag {n}" : $"journalEntry {posting.JournalEntryId}");
        }

        foreach (var project in report.UnlinkedProjects)
        {
            yield return new Anomaly(
                AnomalyTypes.UnlinkedProjects,
                Medium,
                null,
                $"Prosjektet «{project.Name}» har regnskap i {report.Year}, men er ikke koblet til noen budsjettpost.",
                project.Amount,
                $"prosjekt {project.ProjectId}");
        }

        foreach (var project in report.ProjectsLinkedToSeveralLines)
        {
            yield return new Anomaly(
                AnomalyTypes.ProjectsLinkedToSeveralLines,
                High,
                null,
                $"Prosjektet «{project.Name}» er koblet til flere budsjettposter i {report.Year} og telles dobbelt.",
                project.Amount,
                $"prosjekt {project.ProjectId}");
        }
    }

    public static IEnumerable<Anomaly> OverduePurchases(IEnumerable<FikenPurchase> unpaidPurchases, DateOnly today) =>
        unpaidPurchases
            .Where(p => !p.Deleted && !p.Paid && !p.Settled && ParseDate(p.DueDate) is { } due && due < today)
            .Select(p => new Anomaly(
                AnomalyTypes.OverduePurchases,
                High,
                p.DueDate,
                $"Ubetalt leverandørfaktura fra {p.Supplier?.Name ?? "ukjent leverandør"} forfalt {p.DueDate}" +
                (p.Identifier is null ? "" : $" (fakturanr. {p.Identifier})"),
                p.GrossAmount / 100m,
                $"kjøp {p.PurchaseId}"));

    public static IEnumerable<Anomaly> OverdueSales(IEnumerable<FikenSale> unsettledSales, DateOnly today) =>
        unsettledSales
            .Where(s => !s.Deleted && !s.Settled && ParseDate(s.DueDate) is { } due && due < today
                && (s.OutstandingBalance ?? 1) != 0)
            .Select(s => new Anomaly(
                AnomalyTypes.OverdueSales,
                Medium,
                s.DueDate,
                $"Ubetalt salg til {s.Customer?.Name ?? "ukjent kunde"} forfalt {s.DueDate}" +
                (s.SaleNumber is null ? "" : $" (nr. {s.SaleNumber})"),
                s.OutstandingBalance is { } outstanding ? outstanding / 100m : null,
                $"salg {s.SaleId}"));

    public static IEnumerable<Anomaly> PurchasesWithoutAttachment(IEnumerable<FikenPurchase> purchases) =>
        purchases
            .Where(p => !p.Deleted && (p.PurchaseAttachments is null || p.PurchaseAttachments.Count == 0))
            .Select(p => new Anomaly(
                AnomalyTypes.PurchasesWithoutAttachment,
                Medium,
                p.Date,
                $"Kjøp fra {p.Supplier?.Name ?? "ukjent leverandør"} uten vedlegg" +
                (p.Identifier is null ? "" : $" (fakturanr. {p.Identifier})"),
                p.GrossAmount / 100m,
                $"kjøp {p.PurchaseId}"));

    public static IEnumerable<Anomaly> StaleInbox(
        IEnumerable<FikenInboxDocument> unusedInbox,
        IEnumerable<FikenEhfDocument> unprocessedEhf,
        DateOnly today,
        int maxAgeDays)
    {
        foreach (var doc in unusedInbox.Where(d => !d.Status))
        {
            if (ParseDateTime(doc.CreatedAt) is { } created && today.DayNumber - created.DayNumber > maxAgeDays)
            {
                yield return new Anomaly(
                    AnomalyTypes.StaleInbox,
                    Low,
                    created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    $"Innboksdokument «{doc.Name ?? doc.Filename}» er ubehandlet i mer enn {maxAgeDays} dager.",
                    null,
                    $"innboks {doc.DocumentId}");
            }
        }

        foreach (var ehf in unprocessedEhf.Where(e => string.Equals(e.Status, "unprocessed", StringComparison.OrdinalIgnoreCase)))
        {
            var received = ParseDateTime(ehf.CreatedAt) ?? ParseDate(ehf.IssueDate);
            if (received is { } date && today.DayNumber - date.DayNumber > maxAgeDays)
            {
                yield return new Anomaly(
                    AnomalyTypes.StaleInbox,
                    ParseDate(ehf.DueDate) is { } due && due < today ? High : Medium,
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    $"EHF-faktura fra {ehf.SupplierName} (nr. {ehf.InvoiceNumber}, forfall {ehf.DueDate}) er ubehandlet i mer enn {maxAgeDays} dager.",
                    ehf.Gross is { } gross ? gross / 100m : null,
                    $"ehf {ehf.EhfDocumentId}");
            }
        }
    }

    public static IEnumerable<Anomaly> StaleDrafts(IEnumerable<FikenPurchaseDraft> drafts, DateOnly today, int maxAgeDays) =>
        drafts
            .Where(d => ParseDate(d.InvoiceIssueDate) is { } issued && today.DayNumber - issued.DayNumber > maxAgeDays)
            .Select(d => new Anomaly(
                AnomalyTypes.StaleDrafts,
                Low,
                d.InvoiceIssueDate,
                $"Kjøpsutkast fra {d.Contact?.Name ?? "ukjent leverandør"}" +
                (d.InvoiceNumber is null ? "" : $" (fakturanr. {d.InvoiceNumber})") +
                $" med fakturadato for mer enn {maxAgeDays} dager siden er ikke bokført.",
                d.GrossAmount / 100m,
                $"kjøpsutkast {d.DraftId}"));

    /// <summary>Posteringer på prosjekter med dato etter prosjektets sluttdato.</summary>
    public static IEnumerable<Anomaly> PostingsAfterProjectEnd(
        IEnumerable<FikenJournalEntry> entries,
        IReadOnlyList<FikenProject> projects)
    {
        var endDates = projects
            .Where(p => ParseDate(p.EndDate) is not null)
            .GroupBy(p => p.ProjectId)
            .ToDictionary(g => g.Key, g => (Project: g.First(), End: ParseDate(g.First().EndDate)!.Value));

        foreach (var entry in entries)
        {
            if (ParseDate(entry.Date) is not { } date)
            {
                continue;
            }

            foreach (var line in entry.Lines ?? [])
            {
                if (!BudgetReportBuilder.TryGetResultAmount(line, out _, out var amount)
                    || BudgetReportBuilder.PrimaryProjectId(line) is not { } projectId
                    || !endDates.TryGetValue(projectId, out var info)
                    || date <= info.End)
                {
                    continue;
                }

                yield return new Anomaly(
                    AnomalyTypes.PostingsAfterProjectEnd,
                    Low,
                    entry.Date,
                    $"Postering på prosjektet «{info.Project.Name}» etter sluttdato {info.Project.EndDate}: {entry.Description}",
                    amount,
                    entry.JournalEntryNumber is { } n ? $"bilag {n}" : $"journalEntry {entry.JournalEntryId}");
            }
        }
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateOnly? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return DateOnly.FromDateTime(dto.UtcDateTime);
        }

        return ParseDate(value);
    }
}
