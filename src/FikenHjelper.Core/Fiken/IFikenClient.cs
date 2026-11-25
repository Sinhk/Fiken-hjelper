namespace FikenHjelper.Core.Fiken;

public interface IFikenClient
{
    Task<IReadOnlyList<FikenCompany>> GetCompaniesAsync(
        string? apiTokenOverride = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FikenContact>> GetCustomersAsync(
        string companySlug,
        CancellationToken ct = default);

    Task<CreatedResourceResult> CreateCustomerAsync(
        string companySlug,
        FikenCreateContactRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<FikenProject>> GetProjectsAsync(
        string companySlug,
        bool? completed = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<FikenAccount>> GetIncomeAccountsAsync(
        string companySlug,
        CancellationToken ct = default,
        string? apiTokenOverride = null);

    Task<IReadOnlyList<FikenBankAccount>> GetBankAccountsAsync(
        string companySlug,
        CancellationToken ct = default,
        string? apiTokenOverride = null);

    Task<IReadOnlyList<FikenInvoiceDraftSummary>> GetInvoiceDraftsAsync(
        string companySlug,
        CancellationToken ct = default);

    Task<CreatedResourceResult> CreateInvoiceDraftAsync(
        string companySlug,
        FikenInvoiceDraftRequest request,
        CancellationToken ct = default);

    /// <summary>Henter alle posteringer (journal entries) med dato i [from, to], inkludert linjer med konto og prosjekt.</summary>
    Task<IReadOnlyList<FikenJournalEntry>> GetJournalEntriesAsync(
        string companySlug,
        DateOnly from,
        DateOnly to,
        CancellationToken ct = default);
}
