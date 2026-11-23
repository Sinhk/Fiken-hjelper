namespace FikenHjelper.Core.Fiken;

/// <summary>
/// Fiken-kall for innkommende fakturaer og kjøpsutkast (Fase 4). Eneste skrivende kall er
/// opprettelse av kjøpsutkast og vedlegg til kjøpsutkast. Kjøp bokføres aldri herfra.
/// </summary>
public interface IFikenPurchasingClient
{
    /// <param name="status">"unused", "used" eller "all".</param>
    Task<IReadOnlyList<FikenInboxDocument>> GetInboxDocumentsAsync(
        string companySlug,
        string status = "unused",
        string? nameContains = null,
        CancellationToken ct = default);

    Task<FikenInboxDocument> GetInboxDocumentAsync(string companySlug, long inboxDocumentId, CancellationToken ct = default);

    /// <param name="status">"unprocessed", "used", "processed" eller null (alle unntatt slettede).</param>
    Task<IReadOnlyList<FikenEhfDocument>> GetEhfDocumentsAsync(
        string companySlug,
        string? status = "unprocessed",
        DateOnly? issueDateFrom = null,
        CancellationToken ct = default);

    Task<FikenEhfDocument> GetEhfDocumentAsync(string companySlug, long ehfDocumentId, CancellationToken ct = default);

    Task<IReadOnlyList<FikenPurchase>> GetPurchasesAsync(
        string companySlug,
        long? contactId = null,
        DateOnly? dateFrom = null,
        DateOnly? dateTo = null,
        bool? paid = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FikenSale>> GetSalesAsync(
        string companySlug,
        bool? settled = null,
        DateOnly? dateFrom = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FikenPurchaseDraft>> GetPurchaseDraftsAsync(string companySlug, CancellationToken ct = default);

    /// <summary>Oppretter et kjøpsutkast (ikke bokført, ikke betalt).</summary>
    Task<CreatedResourceResult> CreatePurchaseDraftAsync(
        string companySlug,
        FikenPurchaseDraftRequest request,
        CancellationToken ct = default);

    /// <summary>Kobler et eksisterende innboks- eller EHF-dokument til kjøpsutkastet (dokumentet markeres som brukt i Fiken).</summary>
    Task<CreatedResourceResult> AttachDocumentToPurchaseDraftAsync(
        string companySlug,
        long draftId,
        PurchaseDocumentSource source,
        CancellationToken ct = default);

    /// <param name="range">F.eks. "4000-7999" eller "1000-1500, 2000".</param>
    Task<IReadOnlyList<FikenAccount>> GetAccountsAsync(string companySlug, string range, CancellationToken ct = default);

    Task<IReadOnlyList<FikenContact>> GetSuppliersAsync(
        string companySlug,
        string? name = null,
        string? organizationNumber = null,
        CancellationToken ct = default);

    Task<FikenContact> GetContactAsync(string companySlug, long contactId, CancellationToken ct = default);

    /// <summary>Laster ned et dokument fra Fiken. Bare https-adresser hos fiken.no tillates (bearer-token sendes aldri andre steder).</summary>
    Task<FikenDownloadedFile> DownloadDocumentAsync(string documentUrl, long maxBytes, CancellationToken ct = default);
}
