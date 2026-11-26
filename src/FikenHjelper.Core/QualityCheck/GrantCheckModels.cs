namespace FikenHjelper.Core.QualityCheck;

public enum GrantCheckKind
{
    ReportDeadline = 0,
    ApplicationDeadline = 1,
    UsedExceedsGranted = 2,
    NotReceived = 3,
    CostsOutsidePeriod = 4,
    InvalidShares = 5,
    PaymentNotFound = 6
}

/// <summary>
/// Ett funn i tilskuddskontrollen. <paramref name="Amount"/> er i kroner og har ulik betydning per kontroll
/// (gjenstår å motta, overforbruk, kostnader utenfor perioden), eller null når kontrollen ikke har et beløp.
/// </summary>
public sealed record GrantCheckFinding(
    Guid GrantId,
    string GrantName,
    GrantCheckKind Kind,
    string Check,
    string Detail,
    decimal? Amount,
    QualityStatus Status)
{
    /// <summary>Relativ lenke til tilskuddet i appen.</summary>
    public string Href => $"tilskudd/{GrantId}";
}

/// <summary>
/// Seksjonen «Tilskudd» i kvalitetssjekken. <paramref name="MissingDataNote"/> er satt når kontroller som trenger
/// Fiken-data ikke ble kjørt (ingen foretak, eller henting feilet), så en tom liste ikke leses som «alt i orden».
/// </summary>
public sealed record GrantCheckSection(
    IReadOnlyList<GrantCheckFinding> Findings,
    int GrantCount,
    string? MissingDataNote = null,
    string? Error = null)
{
    /// <summary>Seksjonen når tilskuddene ikke kunne leses. Gir synlig varsel i stedet for å skjule seksjonen.</summary>
    public static GrantCheckSection Failed(string message) => new([], 0, null, message);

    /// <summary>Teksten som vises over funnene: feilen hvis tilskuddene ikke kunne leses, ellers merknaden om manglende Fiken-data.</summary>
    public string? Notice => Error ?? MissingDataNote;

    public const string Title = "Tilskudd";

    public const string Key = "tilskudd";

    public int OverdueCount => Findings.Count(f => f.Status == QualityStatus.Overdue);

    public QualityStatus Status => Findings.Count == 0
        ? (Notice is null ? QualityStatus.Ok : QualityStatus.CheckManually)
        : Findings.Max(f => f.Status);
}
