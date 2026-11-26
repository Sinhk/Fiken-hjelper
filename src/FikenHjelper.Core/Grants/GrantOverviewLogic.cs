namespace FikenHjelper.Core.Grants;

public enum GrantDeadlineKind
{
    Application,
    Report
}

public enum GrantDeadlineState
{
    /// <summary>Fristen er innen varselgrensen.</summary>
    Upcoming,

    /// <summary>Fristen er passert.</summary>
    Overdue
}

/// <param name="Days">Dager til fristen (negativt når den er passert).</param>
public sealed record GrantDeadline(GrantDeadlineKind Kind, DateOnly Date, GrantDeadlineState State, int Days)
{
    public string Label => Kind == GrantDeadlineKind.Application ? "Søknadsfrist" : "Rapporteringsfrist";
}

/// <summary>Frister og filtrering for oversikten over tilskudd. Ren logikk.</summary>
public static class GrantOverviewLogic
{
    public const int WarningDays = 30;

    /// <summary>
    /// Frister som er passert eller kommer innen <paramref name="warningDays"/> dager. Ingen frister for avslåtte, trukne
    /// og avsluttede tilskudd. Søknadsfristen gjelder bare før søknaden er sendt, og rapporteringsfristen ikke etter at den er rapportert.
    /// </summary>
    public static IReadOnlyList<GrantDeadline> Deadlines(Grant grant, DateOnly today, int warningDays = WarningDays)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var result = new List<GrantDeadline>();
        if (grant.Status is GrantStatus.Rejected or GrantStatus.Withdrawn or GrantStatus.Closed)
        {
            return result;
        }

        void Add(GrantDeadlineKind kind, DateOnly? date)
        {
            if (date is not { } d)
            {
                return;
            }

            var days = d.DayNumber - today.DayNumber;
            if (days < 0)
            {
                result.Add(new GrantDeadline(kind, d, GrantDeadlineState.Overdue, days));
            }
            else if (days <= warningDays)
            {
                result.Add(new GrantDeadline(kind, d, GrantDeadlineState.Upcoming, days));
            }
        }

        if (grant.Status == GrantStatus.Planned)
        {
            Add(GrantDeadlineKind.Application, grant.ApplicationDeadline);
        }

        if (grant.Status != GrantStatus.Reported)
        {
            Add(GrantDeadlineKind.Report, grant.ReportDeadline);
        }

        return result;
    }

    /// <summary>
    /// Om tilskuddet hører til året: bruksperioden overlapper året (åpne ender regnes som åpne), eller vedtaket er fattet i året.
    /// Uten bruksperiode og vedtak brukes søknadsdato og søknadsfrist, og uten noen datoer vises tilskuddet i alle år.
    /// </summary>
    public static bool OverlapsYear(Grant grant, int year)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);

        if (grant.UseFrom is not null || grant.UseTo is not null)
        {
            var overlaps = (grant.UseFrom is null || grant.UseFrom <= end) && (grant.UseTo is null || grant.UseTo >= start);
            if (overlaps)
            {
                return true;
            }
        }

        if (grant.DecisionDate is { } decision)
        {
            return decision.Year == year;
        }

        if (grant.UseFrom is not null || grant.UseTo is not null)
        {
            return false;
        }

        var fallback = new[] { grant.AppliedDate, grant.ApplicationDeadline, grant.ReportDeadline }.Where(d => d.HasValue).ToList();
        return fallback.Count == 0 || fallback.Any(d => d!.Value.Year == year);
    }

    /// <summary>
    /// Årene det finnes tilskudd for, nyeste først (alltid med <paramref name="currentYear"/>): årene til alle datoene,
    /// og alle år mellom bruksperiodens start og slutt (høyst <see cref="MaxPeriodYears"/> år, så en feil årstall ikke gir tusen valg).
    /// </summary>
    public static IReadOnlyList<int> SelectableYears(IEnumerable<Grant> grants, int currentYear)
    {
        var years = new HashSet<int> { currentYear };
        foreach (var grant in grants)
        {
            foreach (var date in new[] { grant.UseFrom, grant.UseTo, grant.DecisionDate, grant.AppliedDate, grant.ApplicationDeadline, grant.ReportDeadline })
            {
                if (date is { } d)
                {
                    years.Add(d.Year);
                }
            }

            if (grant.UseFrom is { } from && grant.UseTo is { } to && to.Year > from.Year)
            {
                for (var y = from.Year; y <= Math.Min(to.Year, from.Year + MaxPeriodYears); y++)
                {
                    years.Add(y);
                }
            }
        }

        return years.OrderDescending().ToList();
    }

    public const int MaxPeriodYears = 30;

    /// <summary>Mottatt, eller null når koblede utbetalinger ikke kan regnes ut (mangler Fiken-data eller posteringen finnes ikke).</summary>
    public static decimal? ReceivedOrNull(GrantReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var unknown = report.Payments.Any(p => p.Payment.IsLinked && (p.Missing || !report.HasFikenData));
        return unknown ? null : report.Received;
    }

    /// <summary>Gjenstår å motta, eller null når innvilget eller mottatt ikke er kjent.</summary>
    public static decimal? RemainingOrNull(GrantReport report) =>
        ReceivedOrNull(report) is { } received && report.Granted is { } granted ? granted - received : null;

    /// <summary>
    /// Summer for de synlige radene. Hver kolonne teller bare rader som har verdi, og forteller hvor mange det var,
    /// så en delsum kan merkes.
    /// </summary>
    public static GrantTotals Totals(IReadOnlyList<GrantReport> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return new GrantTotals(
            Sum(rows, r => r.Grant.AppliedAmount),
            Sum(rows, r => r.Granted),
            Sum(rows, ReceivedOrNull),
            Sum(rows, r => r.Used),
            Sum(rows, RemainingOrNull));
    }

    private static GrantTotal Sum(IReadOnlyList<GrantReport> rows, Func<GrantReport, decimal?> value)
    {
        var values = rows.Select(value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return new GrantTotal(values.Sum(), values.Count, rows.Count);
    }

    /// <summary>Forklaringer til delsummer, f.eks. «innvilget for 2 av 3 tilskudd». Tom når alle summer er komplette.</summary>
    public static IReadOnlyList<string> PartialNotes(GrantTotals totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        var notes = new List<string>();
        void Add(string name, GrantTotal total)
        {
            if (total.HasValue && total.IsPartial)
            {
                notes.Add($"{name} for {total.Counted} av {total.Total} tilskudd");
            }
        }

        Add("søkt", totals.Applied);
        Add("innvilget", totals.Granted);
        Add("mottatt", totals.Received);
        Add("brukt", totals.Used);
        Add("gjenstår", totals.Remaining);
        return notes;
    }

    /// <summary>Beskrivelse av filteret på norsk, f.eks. «år 2027, status Innvilget», eller null når alt vises.</summary>
    public static string? FilterText(int? year, GrantStatus? status)
    {
        var parts = new List<string>();
        if (year is { } y)
        {
            parts.Add($"år {y}");
        }

        if (status is { } s)
        {
            parts.Add($"status {Grant.StatusText(s)}");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Tilskuddene som passer filteret, i samme rekkefølge som <see cref="Filter"/> gir.</summary>
    public static IReadOnlyList<Grant> SelectGrants(IEnumerable<Grant> grants, int? year, GrantStatus? status) =>
        grants
            .Where(g => (year is null || OverlapsYear(g, year.Value)) && (status is null || g.Status == status))
            .ToList();

    public static IReadOnlyList<GrantReport> Filter(IEnumerable<GrantReport> reports, int? year, GrantStatus? status) =>
        reports
            .Where(r => (year is null || OverlapsYear(r.Grant, year.Value)) && (status is null || r.Grant.Status == status))
            .OrderBy(r => r.Grant.Grantor, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Grant.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>Sum for én kolonne. <see cref="Counted"/> av <see cref="Total"/> rader hadde verdi.</summary>
public sealed record GrantTotal(decimal Sum, int Counted, int Total)
{
    /// <summary>Minst én rad har verdi, så summen kan vises.</summary>
    public bool HasValue => Counted > 0;

    /// <summary>Noen rader mangler verdi, så summen er en delsum.</summary>
    public bool IsPartial => Counted < Total;
}

public sealed record GrantTotals(GrantTotal Applied, GrantTotal Granted, GrantTotal Received, GrantTotal Used, GrantTotal Remaining);

/// <summary>Kopi av tilskudd, så et skjema kan redigere uten å endre det lagrede før det lagres.</summary>
public static class GrantCloner
{
    private static readonly System.Text.Json.JsonSerializerOptions CompareOptions = new();

    /// <summary>Om to tilskudd har samme innhold (sammenlignet felt for felt via JSON, med andelene i fast rekkefølge).</summary>
    public static bool AreEquivalent(Grant a, Grant b) => Canonical(a) == Canonical(b);

    private static string Canonical(Grant g)
    {
        var copy = Clone(g);
        copy.ProjectShares = copy.ProjectShares is null ? null : copy.ProjectShares.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        return System.Text.Json.JsonSerializer.Serialize(copy, CompareOptions);
    }

    public static Grant Clone(Grant g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Grantor = g.Grantor,
        Scheme = g.Scheme,
        Kind = g.Kind,
        Status = g.Status,
        ApplicationDeadline = g.ApplicationDeadline,
        AppliedDate = g.AppliedDate,
        DecisionDate = g.DecisionDate,
        ReportDeadline = g.ReportDeadline,
        UseFrom = g.UseFrom,
        UseTo = g.UseTo,
        AppliedAmount = g.AppliedAmount,
        GrantedAmount = g.GrantedAmount,
        ProjectIds = [.. g.ProjectIds ?? []],
        ProjectShares = g.ProjectShares is null ? null : new Dictionary<long, decimal>(g.ProjectShares),
        AccountRanges = (g.AccountRanges ?? []).Select(r => new AccountRange { From = r.From, To = r.To }).ToList(),
        ManualUses = (g.ManualUses ?? []).Select(u => new GrantManualUse { Date = u.Date, Amount = u.Amount, Description = u.Description }).ToList(),
        Payments = (g.Payments ?? []).Select(p => new GrantPayment { JournalEntryId = p.JournalEntryId, Date = p.Date, Amount = p.Amount, Note = p.Note }).ToList(),
        Link = g.Link,
        Notes = g.Notes
    };
}
