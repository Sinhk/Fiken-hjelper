using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace FikenHjelper.Core.Import;

/// <summary>
/// Leser CSV-eksporter (f.eks. fra Min speiding) med CsvHelper. Her gjøres bare det CsvHelper ikke gjør:
/// gjenkjenning av koding (UTF-8 med/uten BOM, ellers Windows-1252) og valg av skilletegn
/// (semikolon, komma eller tab) ut fra hvilket som gir mest konsistent antall kolonner.
/// </summary>
public static class CsvTableReader
{
    private static readonly string[] CandidateDelimiters = [";", ",", "\t"];
    private const int DelimiterSampleRecords = 20;

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
        text = text.TrimStart('﻿');
        var delimiter = DetectDelimiter(text);
        var (records, _) = ReadRecords(text, delimiter, maxRecords: null, throwOnBadData: true);

        // Første ikke-tomme rad er overskriften.
        if (records.Count == 0)
        {
            throw new InvalidDataException("Filen er tom eller mangler kolonneoverskrifter.");
        }

        var headers = TabularData.MakeUniqueHeaders(records[0]);
        var rows = records
            .Skip(1)
            .Select(r => (IReadOnlyList<string>)r.Select(c => c.Trim()).ToList())
            .ToList();

        return new TabularData(
            headers,
            rows,
            DetectedDelimiter: delimiter == "\t" ? "tab" : delimiter,
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
    /// Velger skilletegnet for de første radene: færrest felt med ødelagte anførselstegn først (et komma-skilt felt
    /// som «"Allergi; diett"» blir ødelagt hvis det leses med semikolon), deretter flest rader med samme antall
    /// kolonner som overskriften (minst to), deretter flest kolonner, deretter semikolon før komma før tab.
    /// </summary>
    internal static string DetectDelimiter(string text)
    {
        var best = ",";
        var bestScore = (BadFields: int.MaxValue, Consistent: -1, Columns: 0);

        foreach (var candidate in CandidateDelimiters)
        {
            var (records, badFields) = ReadRecords(text, candidate, DelimiterSampleRecords, throwOnBadData: false);
            if (records.Count == 0 || records[0].Length < 2)
            {
                continue;
            }

            var columns = records[0].Length;
            var consistent = records.Count(r => r.Length == columns);
            var score = (BadFields: badFields, Consistent: consistent, Columns: columns);
            if (score.BadFields < bestScore.BadFields
                || (score.BadFields == bestScore.BadFields && score.Consistent > bestScore.Consistent)
                || (score.BadFields == bestScore.BadFields && score.Consistent == bestScore.Consistent && score.Columns > bestScore.Columns))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Leser ikke-tomme rader og teller felt CsvHelper melder som ødelagte (BadDataFound).</summary>
    private static (List<string[]> Records, int BadFields) ReadRecords(string text, string delimiter, int? maxRecords, bool throwOnBadData)
    {
        var unclosedQuote = false;
        var badFields = 0;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = false,
            IgnoreBlankLines = true,
            // Et anførselstegn midt i et uquotet felt (f.eks. «12" panne») er vanlig tekst. Et sitert felt som
            // aldri lukkes, sluker derimot resten av filen, så det skal gi feil i stedet for stille datatap.
            BadDataFound = args =>
            {
                badFields++;
                unclosedQuote |= args.Field.TrimStart().StartsWith('"');
            }
        };

        var records = new List<string[]>();
        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, config);
        try
        {
            while ((maxRecords is null || records.Count < maxRecords) && parser.Read())
            {
                var record = parser.Record ?? [];
                if (record.Any(c => !string.IsNullOrWhiteSpace(c)))
                {
                    records.Add(record);
                }
            }
        }
        catch (CsvHelperException ex) when (throwOnBadData)
        {
            throw new InvalidDataException("Filen kunne ikke leses som CSV. Sjekk at riktig fil og format er valgt.", ex);
        }
        catch (CsvHelperException)
        {
            // Ved gjenkjenning av skilletegn regnes et lesebrudd som ødelagte data.
            badFields++;
        }

        if (unclosedQuote && throwOnBadData)
        {
            throw new InvalidDataException("Filen har et anførselstegn som ikke er lukket. Sjekk at riktig fil og format er valgt.");
        }

        return (records, badFields);
    }
}
