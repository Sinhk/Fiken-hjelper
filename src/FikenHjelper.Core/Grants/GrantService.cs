using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>
/// Fiken-data som trengs for å regne på tilskudd: prosjekter, kontoplan og posteringer per år.
/// <see cref="Entries"/> er null uten Fiken-tilkobling, så rapportene viser «mangler Fiken-data».
/// Et år regnes som hentet først når alle posteringene for året er lagret.
/// </summary>
public sealed class GrantFikenData
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, IReadOnlyList<FikenJournalEntry>> _byYear = [];
    private IReadOnlyList<FikenJournalEntry>? _entries;

    public static GrantFikenData None { get; } = new();

    public bool HasCompany { get; init; }

    public string CompanySlug { get; init; } = string.Empty;

    public IReadOnlyList<FikenProject> Projects { get; init; } = [];

    public IReadOnlyList<FikenAccount> Accounts { get; init; } = [];

    /// <summary>Årene som er hentet.</summary>
    public IReadOnlySet<int> Years
    {
        get
        {
            lock (_lock)
            {
                return _byYear.Keys.ToHashSet();
            }
        }
    }

    /// <summary>Alle hentede posteringer (unike), eller null uten Fiken-tilkobling.</summary>
    public IReadOnlyList<FikenJournalEntry>? Entries
    {
        get
        {
            lock (_lock)
            {
                return HasCompany ? _entries ??= [] : null;
            }
        }
    }

    /// <summary>Serialiserer henting, så samtidige kall ikke går i beina på hverandre.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal bool HasYear(int year)
    {
        lock (_lock)
        {
            return _byYear.ContainsKey(year);
        }
    }

    /// <summary>Erstatter årets posteringer og bygger listen på nytt, i samme operasjon.</summary>
    internal void SetYear(int year, IReadOnlyList<FikenJournalEntry> entries)
    {
        lock (_lock)
        {
            _byYear[year] = entries;
            _entries = _byYear.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value).DistinctBy(e => e.JournalEntryId).ToList();
        }
    }
}

/// <summary>Beregnede tall for alle tilskudd, kontrollmeldinger og kontooversikten.</summary>
public sealed record GrantOverview(
    IReadOnlyList<GrantReport> Reports,
    IReadOnlyList<GrantIssue> Issues,
    IReadOnlyList<GrantAccountUsage> AccountUsage);

/// <summary>
/// Laster tilskudd og henter Fiken-data (bare lesing) for årene tilskuddene dekker. Posteringene kommer fra
/// <see cref="BudgetReportService"/>, som mellomlagrer per år.
/// </summary>
public sealed class GrantService(
    IGrantStore store,
    BudgetReportService reports,
    IFikenPurchasingClient purchasing,
    FikenDataCache cache,
    TimeProvider? time = null)
{
    private const string AccountRange = "3000-8799";
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public int CurrentYear => _time.GetLocalNow().Year;

    public DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    public Task<GrantData> LoadAsync(CancellationToken ct = default) => store.LoadAsync(ct);

    /// <summary>Lagrer bare når hele settet er gyldig. Returnerer feilmeldingene (tom liste når lagret).</summary>
    public async Task<IReadOnlyList<string>> SaveAsync(GrantData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var errors = data.Validate();
        if (errors.Count > 0)
        {
            return errors;
        }

        await store.SaveAsync(data, ct);
        return [];
    }

    /// <summary>Hvilke år posteringer må hentes for, samlet for alle tilskuddene.</summary>
    public IReadOnlyList<int> YearsFor(IEnumerable<Grant> grants) =>
        grants.SelectMany(g => GrantReportBuilder.YearsToFetch(g, CurrentYear)).Distinct().Order().ToList();

    /// <summary>
    /// Henter prosjekter, kontoplan og posteringer for <paramref name="years"/>. Uten valgt foretak returneres
    /// data uten posteringer. Fiken-feil kastes videre, så siden kan vise en melding og bruke manuelle tall.
    /// </summary>
    public async Task<GrantFikenData> LoadFikenAsync(IEnumerable<int> years, bool refresh = false, CancellationToken ct = default)
    {
        var slug = await reports.GetCompanySlugAsync(ct);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return GrantFikenData.None;
        }

        var projects = await reports.GetProjectsAsync(slug, refresh, ct);
        var accounts = await cache.GetOrFetchAsync(
            $"grants:accounts:{slug}",
            refresh,
            () => purchasing.GetAccountsAsync(slug, AccountRange, ct),
            ct);
        var data = new GrantFikenData { HasCompany = true, CompanySlug = slug, Projects = projects, Accounts = accounts };
        await EnsureYearsAsync(data, years, refresh, ct);
        return data;
    }

    /// <summary>
    /// Henter posteringer for år som ikke er hentet ennå (alle oppgitte år ved <paramref name="refresh"/>, som da erstatter
    /// årets posteringer) og legger dem til i <paramref name="data"/>. Hvert år lagres for seg når det er fullført, så en feil
    /// midtveis ikke merker år som hentet uten data. Kall mot samme <paramref name="data"/> går i kø.
    /// </summary>
    public async Task EnsureYearsAsync(GrantFikenData data, IEnumerable<int> years, bool refresh = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.HasCompany)
        {
            return;
        }

        await data.Gate.WaitAsync(ct);
        try
        {
            foreach (var year in years.Distinct().Order().Where(y => refresh || !data.HasYear(y)))
            {
                var entries = await reports.GetJournalEntriesAsync(data.CompanySlug, year, refresh, ct);
                data.SetYear(year, entries);
            }
        }
        finally
        {
            data.Gate.Release();
        }
    }

    /// <summary>
    /// Lagrer ett tilskudd atomisk mot den lagrede filen. <paramref name="baseline"/> er tilskuddet slik det var da siden ble åpnet
    /// (null for et nytt tilskudd). Er det lagrede tilskuddet endret eller slettet siden, lagres ikke noe, og en norsk melding returneres.
    /// Hele settet valideres. Tom liste betyr at det er lagret.
    /// </summary>
    public Task<IReadOnlyList<string>> SaveGrantAsync(Grant edited, Grant? baseline, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edited);
        return store.UpdateAsync(data =>
        {
            var index = data.Grants.FindIndex(g => g.Id == edited.Id);
            if (baseline is null)
            {
                if (index >= 0)
                {
                    return ["Tilskuddet finnes allerede i lagringen. Last siden på nytt for å se den lagrede versjonen."];
                }

                data.Grants.Add(edited);
                return [];
            }

            if (index < 0)
            {
                return ["Tilskuddet er slettet siden du åpnet siden, og blir ikke lagret på nytt. Gå til oversikten og legg det inn som et nytt tilskudd om du vil beholde det."];
            }

            if (!GrantCloner.AreEquivalent(data.Grants[index], baseline))
            {
                return ["Tilskuddet er endret et annet sted siden du åpnet siden, så det ble ikke lagret. Last siden på nytt for å se endringene, og gjør dine endringer på nytt."];
            }

            data.Grants[index] = edited;
            return [];
        }, ct);
    }

    /// <summary>Sletter et tilskudd atomisk. Er det allerede slettet, er det i orden.</summary>
    public Task<IReadOnlyList<string>> DeleteGrantAsync(Guid id, CancellationToken ct = default) =>
        store.UpdateAsync(data =>
        {
            data.Grants.RemoveAll(g => g.Id == id);
            return [];
        }, ct);

    public static GrantOverview BuildOverview(IReadOnlyList<Grant> grants, GrantFikenData data)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(data);

        var reportList = GrantReportBuilder.BuildAll(grants, data.Entries, data.Accounts, data.Projects);
        var issues = GrantReportBuilder.CheckProjectShares(grants, data.Projects).ToList();
        // Fordelingsmeldingene står allerede i listen over. Resten (brukt over innvilget, utbetaling mangler) kommer fra rapportene.
        issues.AddRange(reportList
            .SelectMany(r => r.Issues)
            .Where(i => i.Kind is not (GrantIssueKind.ShareMissing or GrantIssueKind.ShareOverAllocated)));
        var usage = data.Entries is null ? [] : GrantAccountOverview.Build(grants, data.Entries, data.Accounts);
        return new GrantOverview(reportList, issues, usage);
    }
}
