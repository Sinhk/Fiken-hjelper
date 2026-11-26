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

    /// <summary>Årene det finnes tilskudd for, nyeste først (alltid med <paramref name="currentYear"/>).</summary>
    public static IReadOnlyList<int> SelectableYears(IEnumerable<Grant> grants, int currentYear)
    {
        var years = new HashSet<int> { currentYear };
        foreach (var grant in grants)
        {
            foreach (var date in new[] { grant.UseFrom, grant.UseTo, grant.DecisionDate, grant.AppliedDate, grant.ApplicationDeadline })
            {
                if (date is { } d)
                {
                    years.Add(d.Year);
                }
            }
        }

        return years.OrderDescending().ToList();
    }

    public static IReadOnlyList<GrantReport> Filter(IEnumerable<GrantReport> reports, int? year, GrantStatus? status) =>
        reports
            .Where(r => (year is null || OverlapsYear(r.Grant, year.Value)) && (status is null || r.Grant.Status == status))
            .OrderBy(r => r.Grant.Grantor, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Grant.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>Kopi av tilskudd, så et skjema kan redigere uten å endre det lagrede før det lagres.</summary>
public static class GrantCloner
{
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
