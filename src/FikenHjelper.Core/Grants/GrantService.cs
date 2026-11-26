using FikenHjelper.Core.Budgeting;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Core.Grants;

/// <summary>
/// Fiken-data som trengs for å regne på tilskudd: prosjekter, kontoplan og posteringer for de årene som er hentet.
/// <see cref="Entries"/> er null uten Fiken-tilkobling, så rapportene viser «mangler Fiken-data».
/// </summary>
public sealed class GrantFikenData
{
    public static GrantFikenData None { get; } = new();

    public bool HasCompany { get; init; }

    public string CompanySlug { get; init; } = string.Empty;

    public IReadOnlyList<FikenProject> Projects { get; init; } = [];

    public IReadOnlyList<FikenAccount> Accounts { get; init; } = [];

    public HashSet<int> Years { get; } = [];

    /// <summary>Alle hentede posteringer (unike), eller null uten Fiken-tilkobling.</summary>
    public IReadOnlyList<FikenJournalEntry>? Entries { get; internal set; }
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

    /// <summary>Henter posteringer for år som ikke er hentet ennå, og legger dem til i <paramref name="data"/>.</summary>
    public async Task EnsureYearsAsync(GrantFikenData data, IEnumerable<int> years, bool refresh = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.HasCompany)
        {
            return;
        }

        var all = (data.Entries ?? []).ToList();
        foreach (var year in years.Distinct().Order().Where(y => refresh || !data.Years.Contains(y)))
        {
            all.AddRange(await reports.GetJournalEntriesAsync(data.CompanySlug, year, refresh, ct));
            data.Years.Add(year);
        }

        data.Entries = all.DistinctBy(e => e.JournalEntryId).ToList();
    }

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
