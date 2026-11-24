using System.Text;

namespace FikenHjelper.Core.Import;

/// <summary>
/// Leser CSV-eksporter (f.eks. fra Min speiding). Gjenkjenner koding (UTF-8 med/uten BOM,
/// ellers Windows-1252) og skilletegn (semikolon, komma eller tab).
/// </summary>
public static class CsvTableReader
{
    private static readonly char[] CandidateDelimiters = [';', ',', '\t'];

    static CsvTableReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static TabularData Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var (text, encodingName) = Decode(buffer.ToArray());
        return Parse(text, encodingName);
    }

    public static TabularData Parse(string text, string? encodingName = null)
    {
        text = text.TrimStart('\uFEFF');
        var delimiter = DetectDelimiter(text);
        var records = SplitRecords(text, delimiter);

        // Hopp over tomme linjer før overskriften.
        var headerIndex = records.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0)
        {
            throw new InvalidDataException("Filen er tom eller mangler kolonneoverskrifter.");
        }

        var headers = TabularData.MakeUniqueHeaders(records[headerIndex]);
        var rows = records
            .Skip(headerIndex + 1)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .Select(r => (IReadOnlyList<string>)r.Select(c => c.Trim()).ToList())
            .ToList();

        return new TabularData(
            headers,
            rows,
            DetectedDelimiter: delimiter == '\t' ? "tab" : delimiter.ToString(),
            DetectedEncoding: encodingName);
    }

    internal static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8 (BOM)");
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        }

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(1252).GetString(bytes), "Windows-1252");
        }
    }

    /// <summary>
    /// Velger skilletegnet som gir flest rader med samme antall kolonner som overskriften (minst to kolonner).
    /// Bare de første radene vurderes. Ved likt resultat vinner flest kolonner, deretter semikolon før komma før tab.
    /// </summary>
    internal static char DetectDelimiter(string text)
    {
        var sample = text.Length > 64_000 ? text[..64_000] : text;
        var best = ',';
        var bestScore = (Consistent: -1, Columns: 0);

        foreach (var candidate in CandidateDelimiters)
        {
            List<List<string>> records;
            try
            {
                records = SplitRecords(sample, candidate, throwOnUnclosedQuote: false)
                    .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
                    .Take(20)
                    .ToList();
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (records.Count == 0 || records[0].Count < 2)
            {
                continue;
            }

            var columns = records[0].Count;
            var consistent = records.Count(r => r.Count == columns);
            if (consistent > bestScore.Consistent
                || (consistent == bestScore.Consistent && columns > bestScore.Columns))
            {
                best = candidate;
                bestScore = (consistent, columns);
            }
        }

        return best;
    }

    internal static List<List<string>> SplitRecords(string text, char delimiter, bool throwOnUnclosedQuote = true)
    {
        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(ch);
                i++;
                continue;
            }

            if (ch == '"' && field.ToString().Trim().Length == 0)
            {
                // Anførselstegn åpner bare et sitert felt når det står først i feltet (RFC 4180).
                field.Clear();
                inQuotes = true;
                i++;
            }
            else if (ch == delimiter)
            {
                current.Add(field.ToString());
                field.Clear();
                i++;
            }
            else if (ch == '\r' || ch == '\n')
            {
                current.Add(field.ToString());
                field.Clear();
                records.Add(current);
                current = [];
                i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            }
            else
            {
                field.Append(ch);
                i++;
            }
        }

        if (inQuotes && throwOnUnclosedQuote)
        {
            throw new InvalidDataException("Filen har et anførselstegn som ikke er lukket. Sjekk at riktig fil og format er valgt.");
        }

        if (field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            records.Add(current);
        }

        return records;
    }
}
