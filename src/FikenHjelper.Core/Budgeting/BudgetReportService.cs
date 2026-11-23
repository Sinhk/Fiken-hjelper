using System.Collections.Concurrent;
using FikenHjelper.Core.Config;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Budgeting;

/// <summary>
/// Henter prosjekter og posteringer fra Fiken og bygger rapporter. Fiken-data mellomlagres i minnet
/// en stund, slik at bytte av visning og eksport ikke gir nye kall mot API-et.
/// </summary>
public sealed class BudgetReportService
{
    private readonly IFikenClient _fiken;
    private readonly IBudgetStore _budgetStore;
    private readonly ILocalConfigStore _configStore;
    private readonly FikenDataCache _cache;

    public BudgetReportService(
        IFikenClient fiken,
        IBudgetStore budgetStore,
        ILocalConfigStore configStore,
        FikenDataCache cache)
    {
        _fiken = fiken;
        _budgetStore = budgetStore;
        _configStore = configStore;
        _cache = cache;
    }

    public async Task<string> GetCompanySlugAsync(CancellationToken ct = default)
    {
        var settings = await _configStore.LoadSettingsAsync(ct);
        return settings.FikenCompanySlug;
    }

    public Task<IReadOnlyList<FikenProject>> GetProjectsAsync(string companySlug, bool refresh = false, CancellationToken ct = default) =>
        _cache.GetOrFetchAsync(
            $"projects:{companySlug}",
            refresh,
            () => _fiken.GetProjectsAsync(companySlug, completed: null, ct));

    public Task<IReadOnlyList<FikenJournalEntry>> GetJournalEntriesAsync(string companySlug, int year, bool refresh = false, CancellationToken ct = default) =>
        _cache.GetOrFetchAsync(
            $"journal:{companySlug}:{year}",
            refresh,
            () => _fiken.GetJournalEntriesAsync(companySlug, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct));

    /// <summary>
    /// Bygger sammenligning for de oppgitte årene. Fiken-data hentes bare for år der minst én post
    /// bruker prosjekter. Andre år vises med manuelle tall.
    /// </summary>
    public async Task<BudgetComparison> BuildComparisonAsync(
        IEnumerable<int> years,
        bool refresh = false,
        CancellationToken ct = default)
    {
        var budget = await _budgetStore.LoadAsync(ct);
        var slug = await GetCompanySlugAsync(ct);
        var hasFiken = !string.IsNullOrWhiteSpace(slug);
        var projects = hasFiken ? await GetProjectsAsync(slug, refresh, ct) : [];

        var reports = new List<BudgetYearReport>();
        foreach (var year in years.Distinct())
        {
            var budgetYear = budget.FindYear(year) ?? new BudgetYear { Year = year };
            var needsFiken = hasFiken && budgetYear.Lines.Any(l => l.ManualActual is null);
            var entries = needsFiken ? await GetJournalEntriesAsync(slug, year, refresh, ct) : null;
            reports.Add(BudgetReportBuilder.Build(budgetYear, projects, entries));
        }

        return BudgetComparison.Build(reports);
    }
}

/// <summary>Mellomlager for Fiken-data, delt mellom sider og eksport (singleton).</summary>
public sealed class FikenDataCache(TimeProvider? time = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, object Value)> _entries = new();

    public async Task<IReadOnlyList<T>> GetOrFetchAsync<T>(
        string key,
        bool refresh,
        Func<Task<IReadOnlyList<T>>> fetch)
    {
        var now = _time.GetUtcNow();
        if (!refresh
            && _entries.TryGetValue(key, out var cached)
            && now - cached.FetchedAt < Lifetime
            && cached.Value is IReadOnlyList<T> value)
        {
            return value;
        }

        var fresh = await fetch();
        _entries[key] = (now, fresh);
        return fresh;
    }

    public void Clear() => _entries.Clear();
}
