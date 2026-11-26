namespace FikenHjelper.Core.QualityCheck;

/// <summary>Status for et enkelt funn eller en hel seksjon i kvalitetssjekken.</summary>
public enum QualityStatus
{
    /// <summary>Ingenting å følge opp.</summary>
    Ok = 0,

    /// <summary>Åpen post som ikke er forfalt ennå.</summary>
    Open = 1,

    /// <summary>Negativ saldo, f.eks. overbetaling eller kreditnota som ikke er motregnet.</summary>
    Credit = 2,

    /// <summary>Beløpet kan ikke regnes ut sikkert (f.eks. fremmed valuta eller fakturasalg uten treff). Må sjekkes i Fiken.</summary>
    CheckManually = 3,

    /// <summary>Åpen post som er forfalt.</summary>
    Overdue = 4
}

public static class QualitySectionKeys
{
    public const string CustomerInvoices = "kundefakturaer";
    public const string OtherSales = "annet-salg";
    public const string SupplierPurchases = "leverandorkjop";
}

/// <summary>
/// Én åpen post: faktura, salg eller kjøp. Beløp i øre; <c>null</c> betyr at beløpet ikke kunne regnes ut.
/// </summary>
public sealed record QualityFinding(
    string Reference,
    string Counterparty,
    DateOnly? Date,
    DateOnly? DueDate,
    int? DaysOverdue,
    long? OutstandingOre,
    string? Project,
    QualityStatus Status,
    long? FikenId = null,
    string? Note = null);

public sealed record QualityCheckSection(
    string Key,
    string Title,
    IReadOnlyList<QualityFinding> Findings)
{
    /// <summary>Sum av kjente beløp (kreditsaldoer trekkes fra).</summary>
    public long TotalOre => Findings.Sum(f => f.OutstandingOre ?? 0);

    public long OverdueOre => Findings.Where(f => f.Status == QualityStatus.Overdue).Sum(f => f.OutstandingOre ?? 0);

    /// <summary>Antall poster der beløpet ikke kunne regnes ut og ikke er med i summen.</summary>
    public int UnknownAmountCount => Findings.Count(f => f.OutstandingOre is null);

    public int OverdueCount => Findings.Count(f => f.Status == QualityStatus.Overdue);

    public QualityStatus Status => Findings.Count == 0
        ? QualityStatus.Ok
        : Findings.Max(f => f.Status);
}

/// <summary>
/// Resultatet av en kvalitetssjekk. <see cref="ReferenceDate"/> bestemmer hva som regnes som forfalt,
/// og poster datert etter denne datoen tas ikke med. Selve listen over åpne poster er slik den er i Fiken
/// ved <see cref="FetchedAt"/>: en post som er betalt etter referansedatoen, vises ikke, og delbetalinger
/// etter referansedatoen er trukket fra beløpet.
/// </summary>
public sealed record QualityCheckReport(
    DateOnly ReferenceDate,
    DateTimeOffset FetchedAt,
    IReadOnlyList<QualityCheckSection> Sections,
    GrantCheckSection? Grants = null)
{
    public QualityStatus Status
    {
        get
        {
            var statuses = Sections.Select(s => s.Status).ToList();
            if (Grants is not null)
            {
                statuses.Add(Grants.Status);
            }

            return statuses.Count == 0 ? QualityStatus.Ok : statuses.Max();
        }
    }
}
