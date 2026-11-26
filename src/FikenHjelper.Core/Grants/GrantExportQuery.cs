using System.Globalization;
using FikenHjelper.Core.Budgeting;

namespace FikenHjelper.Core.Grants;

/// <summary>Gyldige parametere til <c>/tilskudd/eksport</c>: enten ett tilskudd (<see cref="Id"/>) eller oversikten med filter.</summary>
public sealed record GrantExportQuery(Guid? Id, int? Year, GrantStatus? Status)
{
    /// <summary>
    /// Leser parametrene selv, så feil gir en norsk melding. Tomme verdier regnes som ikke oppgitt. Status må være et av
    /// enum-navnene (f.eks. <c>Granted</c>), ikke et tall eller en liste.
    /// </summary>
    public static bool TryParse(string? id, string? year, string? status, out GrantExportQuery query, out string error)
    {
        query = new GrantExportQuery(null, null, null);
        error = string.Empty;
        Guid? parsedId = null;
        int? parsedYear = null;
        GrantStatus? parsedStatus = null;

        if (!string.IsNullOrWhiteSpace(id))
        {
            if (!Guid.TryParse(id.Trim(), out var g))
            {
                error = "Ugyldig id. Bruk id-en til tilskuddet slik den står i adressen på tilskuddssiden.";
                return false;
            }

            parsedId = g;
        }

        if (!string.IsNullOrWhiteSpace(year))
        {
            if (!int.TryParse(year.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                || y is < BudgetData.MinYear or > BudgetData.MaxYear)
            {
                error = $"Året må være et tall mellom {BudgetData.MinYear} og {BudgetData.MaxYear}.";
                return false;
            }

            parsedYear = y;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var name = Enum.GetNames<GrantStatus>().FirstOrDefault(n => n.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                error = $"Ukjent status. Gyldige verdier er {string.Join(", ", Enum.GetNames<GrantStatus>())}.";
                return false;
            }

            parsedStatus = Enum.Parse<GrantStatus>(name);
        }

        query = new GrantExportQuery(parsedId, parsedYear, parsedStatus);
        return true;
    }
}
