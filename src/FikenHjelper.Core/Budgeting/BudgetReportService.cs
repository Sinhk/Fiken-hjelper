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
            () => _fiken.GetProjectsAsync(companySlug, completed: null, ct),
            ct);

    public Task<IReadOnlyList<FikenJournalEntry>> GetJournalEntriesAsync(string companySlug, int year, bool refresh = false, CancellationToken ct = default) =>
        _cache.GetOrFetchAsync(
            $"journal:{companySlug}:{year}",
            refresh,
            () => _fiken.GetJournalEntriesAsync(companySlug, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), ct),
            ct);

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
        var budgetYears = years.Distinct()
            .Select(year => budget.FindYear(year) ?? new BudgetYear { Year = year })
            .ToList();

        // Prosjekter trengs bare når minst ett år hentes fra Fiken (eller har prosjekter som skal vises).
        bool NeedsFiken(BudgetYear y) => hasFiken && y.Lines.Any(l => l.ManualActual is null);
        var projects = hasFiken && budgetYears.Any(y => NeedsFiken(y) || y.Lines.Any(l => l.ProjectIds.Count > 0))
            ? await GetProjectsAsync(slug, refresh, ct)
            : [];

        var reports = new List<BudgetYearReport>();
        foreach (var budgetYear in budgetYears)
        {
            var entries = NeedsFiken(budgetYear) ? await GetJournalEntriesAsync(slug, budgetYear.Year, refresh, ct) : null;
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
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly Lock _lock = new();
    private long _version;
    private long _clearGeneration;

    private sealed record Entry(DateTimeOffset FetchedAt, long Version, object Value);

    public async Task<IReadOnlyList<T>> GetOrFetchAsync<T>(
        string key,
        bool refresh,
        Func<Task<IReadOnlyList<T>>> fetch,
        CancellationToken ct = default)
    {
        if (!refresh && TryGetFresh<T>(key, out var hit, out _))
        {
            return hit;
        }

        // Ved refresh: en henting som er fullført etter at vi spurte, er fersk nok til å deles.
        var seenVersion = _entries.TryGetValue(key, out var seen) ? seen.Version : 0;

        // Én henting per nøkkel om gangen, så samtidige forespørsler (side + eksport) deler resultatet.
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (TryGetFresh<T>(key, out var shared, out var version) && (!refresh || version > seenVersion))
            {
                return shared;
            }

            long generation;
            lock (_lock)
            {
                generation = _clearGeneration;
            }

            var fresh = await fetch();

            // Er cachen tømt mens vi hentet (f.eks. frakobling av Fiken), skal svaret ikke lagres.
            // Sjekk og lagring skjer under samme lås som Clear(), så ingen Clear() kan slippe imellom.
            lock (_lock)
            {
                if (_clearGeneration == generation)
                {
                    _entries[key] = new Entry(_time.GetUtcNow(), ++_version, fresh);
                }
            }

            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh<T>(string key, out IReadOnlyList<T> value, out long version)
    {
        if (_entries.TryGetValue(key, out var cached)
            && _time.GetUtcNow() - cached.FetchedAt < Lifetime
            && cached.Value is IReadOnlyList<T> list)
        {
            value = list;
            version = cached.Version;
            return true;
        }

        value = [];
        version = 0;
        return false;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _clearGeneration++;
            _entries.Clear();
        }
    }
}
