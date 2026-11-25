using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Mcp.Services;

/// <summary>Mellomlagrede oppslag (15 min) som ellers ville gitt mange kall mot Fiken.</summary>
public sealed class FikenLookups(IFikenPurchasingClient purchasing, IFikenClient fiken, FikenDataCache cache)
{
    public const string ExpenseRange = "4000-7999";
    public const string AllAccountsRange = "1000-8999";

    public Task<IReadOnlyList<FikenAccount>> GetAccountsAsync(string slug, string range, CancellationToken ct, bool refresh = false) =>
        cache.GetOrFetchAsync($"mcp:accounts:{slug}:{range}", refresh, () => purchasing.GetAccountsAsync(slug, range, ct), ct);

    /// <summary>Samme nøkkel som <see cref="BudgetReportService.GetProjectsAsync"/>, så cachen deles.</summary>
    public Task<IReadOnlyList<FikenProject>> GetProjectsAsync(string slug, CancellationToken ct, bool refresh = false) =>
        cache.GetOrFetchAsync($"projects:{slug}", refresh, () => fiken.GetProjectsAsync(slug, completed: null, ct), ct);

    public Task<IReadOnlyList<FikenContact>> GetSuppliersAsync(string slug, CancellationToken ct, bool refresh = false) =>
        cache.GetOrFetchAsync($"mcp:suppliers:{slug}", refresh, () => purchasing.GetSuppliersAsync(slug, ct: ct), ct);
}
