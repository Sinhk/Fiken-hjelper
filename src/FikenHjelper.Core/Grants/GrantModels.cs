using System.Globalization;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Grants;

/// <summary>Alle søknader og tilskudd, lagret lokalt i <c>grants.json</c>.</summary>
public sealed class GrantData
{
    public List<Grant> Grants { get; set; } = [];

    public Grant? Find(Guid id) => Grants.FirstOrDefault(g => g.Id == id);

    /// <summary>
    /// Kontrollerer alle tilskudd, og at ingen Fiken-postering er koblet til mer enn ett tilskudd
    /// (heller ikke to ganger til samme tilskudd).
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var grant in Grants)
        {
            errors.AddRange(grant.Validate());
        }

        var duplicates = Grants
            .SelectMany(g => (g.Payments ?? []).Where(p => p.JournalEntryId is > 0).Select(p => (Id: p.JournalEntryId!.Value, Grant: g)))
            .GroupBy(x => x.Id)
            .Where(g => g.Select(x => x.Grant).Distinct().Count() > 1);
        foreach (var group in duplicates)
        {
            var names = string.Join(", ", group.Select(x => $"«{x.Grant.DisplayName}»").Distinct());
            errors.Add($"Fiken-posteringen {group.Key} er koblet til flere utbetalinger: {names}. En postering kan bare kobles én gang.");
        }

        return errors;
    }
}

public enum GrantKind
{
    /// <summary>Prosjekttilskudd: koblet til ett eller flere Fiken-prosjekter.</summary>
    Project,

    /// <summary>Driftstilskudd: uten prosjekt, bruken leses fra kontoer og/eller registreres manuelt.</summary>
    Operating
}

public enum GrantStatus
{
    Planned,
    Submitted,
    Granted,
    PartiallyGranted,
    Rejected,
    Withdrawn,
    Reported,
    Closed
}

/// <summary>Kontointervall (begge ender med), f.eks. 6800–6899.</summary>
public sealed class AccountRange
{
    public int From { get; set; }
    public int To { get; set; }

    public bool Contains(int account) => account >= From && account <= To;
}

/// <summary>Manuelt registrert bruk av et driftstilskudd.</summary>
public sealed class GrantManualUse
{
    public DateOnly? Date { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// En mottatt utbetaling. Enten koblet til en Fiken-postering (<see cref="JournalEntryId"/>, og beløpet hentes da fra Fiken)
/// eller en manuell linje med dato, beløp og notat.
/// </summary>
public sealed class GrantPayment
{
    public long? JournalEntryId { get; set; }

    /// <summary>Dato for posteringen (lagret ved kobling, så riktig år kan hentes) eller for en manuell utbetaling.</summary>
    public DateOnly? Date { get; set; }

    /// <summary>Beløp for manuelle utbetalinger. Ignoreres når <see cref="JournalEntryId"/> er satt.</summary>
    public decimal? Amount { get; set; }

    public string? Note { get; set; }

    [JsonIgnore]
    public bool IsLinked => JournalEntryId is > 0;
}

/// <summary>Én søknad / ett tilskudd.</summary>
public sealed class Grant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Grantor { get; set; } = string.Empty;
    public string? Scheme { get; set; }
    public GrantKind Kind { get; set; } = GrantKind.Project;
    public GrantStatus Status { get; set; } = GrantStatus.Planned;

    public DateOnly? ApplicationDeadline { get; set; }
    public DateOnly? AppliedDate { get; set; }
    public DateOnly? DecisionDate { get; set; }
    public DateOnly? ReportDeadline { get; set; }
    public DateOnly? UseFrom { get; set; }
    public DateOnly? UseTo { get; set; }

    public decimal? AppliedAmount { get; set; }
    public decimal? GrantedAmount { get; set; }

    public List<long> ProjectIds { get; set; } = [];

    /// <summary>Prosent (0–100) av prosjektets kostnader som regnes mot dette tilskuddet. Mangler: hele prosjektet.</summary>
    public Dictionary<long, decimal>? ProjectShares { get; set; }

    public List<AccountRange> AccountRanges { get; set; } = [];
    public List<GrantManualUse> ManualUses { get; set; } = [];
    public List<GrantPayment> Payments { get; set; } = [];

    public string? Link { get; set; }
    public string? Notes { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(uten navn)" : Name.Trim();

    /// <summary>Andelen (prosent) av prosjektet som regnes mot dette tilskuddet, eller null når den ikke er satt.</summary>
    public decimal? ShareOf(long projectId) =>
        ProjectShares is not null && ProjectShares.TryGetValue(projectId, out var share) ? share : null;

    /// <summary>Om tilskuddet er aktivt, altså ikke avslått eller trukket.</summary>
    [JsonIgnore]
    public bool IsActive => Status is not (GrantStatus.Rejected or GrantStatus.Withdrawn);

    /// <summary>Norske feilmeldinger for ting som ikke henger sammen. Tom liste når alt er i orden.</summary>
    public IReadOnlyList<string> Validate()
    {
        var nb = CultureInfo.GetCultureInfo("nb-NO");
        var prefix = string.IsNullOrWhiteSpace(Name) ? string.Empty : $"«{Name.Trim()}»: ";
        var errors = new List<string>();
        void Add(string message) => errors.Add(prefix + message);

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Tilskuddet mangler navn.");
        }

        if (Status is GrantStatus.Rejected or GrantStatus.Withdrawn && GrantedAmount is not null)
        {
            Add($"Innvilget beløp kan ikke være satt når status er {StatusText(Status)}.");
        }

        if (AppliedAmount < 0)
        {
            Add("Søkt beløp kan ikke være negativt.");
        }

        if (GrantedAmount < 0)
        {
            Add("Innvilget beløp kan ikke være negativt.");
        }

        if (UseFrom is { } from && UseTo is { } to && from > to)
        {
            Add($"Bruksperioden starter ({from:dd.MM.yyyy}) etter at den slutter ({to:dd.MM.yyyy}).");
        }

        foreach (var range in AccountRanges ?? [])
        {
            if (range.From <= 0 || range.To <= 0)
            {
                Add("Kontointervall må ha gyldige kontonumre.");
            }
            else if (range.From > range.To)
            {
                Add($"Kontointervallet {range.From}–{range.To} starter etter at det slutter.");
            }
        }

        if (Kind == GrantKind.Operating && ((ProjectIds?.Count ?? 0) > 0 || (ProjectShares?.Count ?? 0) > 0))
        {
            Add("Prosjekter og andeler gjelder bare prosjekttilskudd.");
        }

        if (Kind == GrantKind.Project && ((AccountRanges?.Count ?? 0) > 0 || (ManualUses?.Count ?? 0) > 0))
        {
            Add("Kontointervall og manuell bruk gjelder bare driftstilskudd.");
        }

        foreach (var (projectId, share) in ProjectShares ?? [])
        {
            if (share is <= 0 or > 100)
            {
                Add($"Andelen for prosjekt {projectId} må være mellom 0 og 100 % (er {share.ToString("0.##", nb)} %).");
            }
            else if (!(ProjectIds ?? []).Contains(projectId))
            {
                Add($"Andelen for prosjekt {projectId} hører til et prosjekt som ikke er koblet til tilskuddet.");
            }
        }

        foreach (var use in ManualUses ?? [])
        {
            if (use.Amount <= 0)
            {
                Add("Manuell bruk må ha et beløp større enn 0.");
            }
        }

        foreach (var payment in Payments ?? [])
        {
            if (!payment.IsLinked && payment.Amount is not > 0)
            {
                Add("En manuell utbetaling må ha et beløp større enn 0.");
            }
        }

        var duplicateIds = (Payments ?? [])
            .Where(p => p.IsLinked)
            .GroupBy(p => p.JournalEntryId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var id in duplicateIds)
        {
            Add($"Fiken-posteringen {id} er koblet flere ganger til samme tilskudd.");
        }

        return errors;
    }

    public static string StatusText(GrantStatus status) => status switch
    {
        GrantStatus.Planned => "Planlagt",
        GrantStatus.Submitted => "Sendt",
        GrantStatus.Granted => "Innvilget",
        GrantStatus.PartiallyGranted => "Delvis innvilget",
        GrantStatus.Rejected => "Avslått",
        GrantStatus.Withdrawn => "Trukket",
        GrantStatus.Reported => "Rapportert",
        GrantStatus.Closed => "Avsluttet",
        _ => status.ToString()
    };

    public static string KindText(GrantKind kind) =>
        kind == GrantKind.Operating ? "Driftstilskudd" : "Prosjekttilskudd";
}
