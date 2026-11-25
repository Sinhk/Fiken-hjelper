namespace FikenHjelper.Core.Import;

/// <summary>
/// En tabell lest fra en opplastet fil (CSV eller XLSX). Overskriftene er unike:
/// like overskrifter får suffiks som «E-post (2)».
/// </summary>
public sealed record TabularData(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? DetectedDelimiter = null,
    string? DetectedEncoding = null,
    IReadOnlyList<string>? SheetNames = null,
    string? SelectedSheet = null)
{
    public string GetCell(IReadOnlyList<string> row, int columnIndex)
        => columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : string.Empty;

    public int IndexOf(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return -1;
        }

        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], header.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Gjør overskrifter unike og fyller inn navn på tomme overskrifter.</summary>
    public static IReadOnlyList<string> MakeUniqueHeaders(IEnumerable<string?> rawHeaders)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var raw in rawHeaders)
        {
            index++;
            var baseName = string.IsNullOrWhiteSpace(raw) ? $"Kolonne {index}" : raw.Trim();
            var name = baseName;
            var n = 2;
            while (!seen.Add(name))
            {
                name = $"{baseName} ({n++})";
            }

            result.Add(name);
        }

        return result;
    }
}
