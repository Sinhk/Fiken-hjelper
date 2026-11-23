namespace FikenHjelper.Core.Import;

/// <summary>
/// Kobling fra kolonneoverskrifter i en eksportfil til feltene i deltakermodellen.
/// Kolonner lagres som overskriftsnavn (ikke indeks) slik at profilen virker på neste eksport
/// selv om rekkefølgen endres. Inneholder aldri persondata.
/// </summary>
public sealed class ImportProfile
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Fullt navn i én kolonne (f.eks. «Navn»). Brukes hvis fornavn/etternavn ikke er satt.</summary>
    public string? FullNameColumn { get; set; }
    public string? FirstNameColumn { get; set; }
    public string? LastNameColumn { get; set; }
    public string? MemberNoColumn { get; set; }

    /// <summary>E-postkolonner i prioritert rekkefølge; første utfylte verdi brukes.</summary>
    public List<string> EmailColumns { get; set; } = [];

    public string? GroupColumn { get; set; }
    public string? GroupIdColumn { get; set; }
    public string? CategoryColumn { get; set; }
    public string? AmountColumn { get; set; }
    public string? QuantityColumn { get; set; }

    /// <summary>Deltakeren regnes som avmeldt når denne kolonnen er utfylt (f.eks. «Avmeldingsdato»).</summary>
    public string? CancelledDateColumn { get; set; }

    /// <summary>Statuskolonne der verdiene i <see cref="CancelledValues"/> betyr avmeldt.</summary>
    public string? StatusColumn { get; set; }
    public List<string> CancelledValues { get; set; } = [];

    /// <summary>Ja/nei-kolonne for bekreftet. Ikke satt = alle regnes som bekreftet.</summary>
    public string? ConfirmedColumn { get; set; }

    /// <summary>Ja/nei-kolonne for oppmøtt/til stede. Ikke satt = alle regnes som oppmøtt.</summary>
    public string? AttendedColumn { get; set; }

    public string? RegistrationDateColumn { get; set; }

    /// <summary>Gruppenavn som brukes når gruppekolonnen mangler eller er tom.</summary>
    public string? DefaultGroupName { get; set; }

    /// <summary>Kolonner som tas med som svar/tilleggsinfo og vises i deltakertabellen.</summary>
    public List<string> ExtraColumns { get; set; } = [];

    public ImportProfile Clone() => new()
    {
        Name = Name,
        FullNameColumn = FullNameColumn,
        FirstNameColumn = FirstNameColumn,
        LastNameColumn = LastNameColumn,
        MemberNoColumn = MemberNoColumn,
        EmailColumns = [.. EmailColumns],
        GroupColumn = GroupColumn,
        GroupIdColumn = GroupIdColumn,
        CategoryColumn = CategoryColumn,
        AmountColumn = AmountColumn,
        QuantityColumn = QuantityColumn,
        CancelledDateColumn = CancelledDateColumn,
        StatusColumn = StatusColumn,
        CancelledValues = [.. CancelledValues],
        ConfirmedColumn = ConfirmedColumn,
        AttendedColumn = AttendedColumn,
        RegistrationDateColumn = RegistrationDateColumn,
        DefaultGroupName = DefaultGroupName,
        ExtraColumns = [.. ExtraColumns]
    };

    /// <summary>Alle kolonnenavn profilen refererer til (for å telle treff mot en fil).</summary>
    public IEnumerable<string> ReferencedColumns()
    {
        string?[] singles =
        [
            FullNameColumn, FirstNameColumn, LastNameColumn, MemberNoColumn, GroupColumn, GroupIdColumn,
            CategoryColumn, AmountColumn, QuantityColumn, CancelledDateColumn, StatusColumn,
            ConfirmedColumn, AttendedColumn, RegistrationDateColumn
        ];

        return singles
            .Concat(EmailColumns)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class ImportProfilesData
{
    public List<ImportProfile> Profiles { get; set; } = [];
}
