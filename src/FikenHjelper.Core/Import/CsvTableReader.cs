using System.Text;
using CsvHelper;

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
        var (records, _, _) = ReadRecords(text, delimiter, maxRecords: null, throwOnBadData: true);

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
    /// Velger skilletegnet for de første radene: færrest rader med ødelagt sitering først (et komma-skilt felt
    /// som «"Allergi; diett"» blir ødelagt hvis det leses med semikolon), deretter færrest rader med løse
    /// anførselstegn (som i «12" panne»), deretter flest rader med samme antall kolonner som overskriften
    /// (minst to), deretter flest kolonner, deretter semikolon før komma før tab.
    /// </summary>
    internal static string DetectDelimiter(string text)
    {
        var best = ",";
        var bestScore = (BrokenRecords: int.MaxValue, StrayQuoteRecords: int.MaxValue, Consistent: -1, Columns: 0);

        foreach (var candidate in CandidateDelimiters)
        {
            var (records, brokenRecords, strayQuoteRecords) = ReadRecords(text, candidate, DelimiterSampleRecords, throwOnBadData: false);
            if (records.Count == 0 || records[0].Length < 2)
            {
                continue;
            }

            var columns = records[0].Length;
            var consistent = records.Count(r => r.Length == columns);
            var score = (BrokenRecords: brokenRecords, StrayQuoteRecords: strayQuoteRecords, Consistent: consistent, Columns: columns);
            if (IsBetter(score, bestScore))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;

        static bool IsBetter(
            (int BrokenRecords, int StrayQuoteRecords, int Consistent, int Columns) score,
            (int BrokenRecords, int StrayQuoteRecords, int Consistent, int Columns) best)
        {
            if (score.BrokenRecords != best.BrokenRecords)
            {
                return score.BrokenRecords < best.BrokenRecords;
            }

            if (score.StrayQuoteRecords != best.StrayQuoteRecords)
            {
                return score.StrayQuoteRecords < best.StrayQuoteRecords;
            }

            if (score.Consistent != best.Consistent)
            {
                return score.Consistent > best.Consistent;
            }

            return score.Columns > best.Columns;
        }
    }

    /// <summary>
    /// Leser ikke-tomme rader. Teller rader med ødelagt sitering (se <see cref="HasBrokenQuoting"/>) og rader med
    /// løse anførselstegn i uquotede felt hver for seg.
    /// </summary>
    private static (List<string[]> Records, int BrokenRecords, int StrayQuoteRecords) ReadRecords(
        string text, string delimiter, int? maxRecords, bool throwOnBadData)
    {
        var brokenRecords = 0;
        var strayQuoteRecords = 0;
        var options = new CsvReaderOptions
        {
            Separator = delimiter[0],
            HasHeader = false,
            SkipEmptyLines = true,
            // Et anførselstegn midt i et uquotet felt (f.eks. «12" panne») er vanlig tekst. Ødelagt sitering
            // sjekkes selv per felt (HasBrokenQuoting), siden CsvHelper bare melder første feil i hver rad.
            MalformedData = CsvMalformedDataHandling.Recover,
            OnMalformedRecord = record =>
            {
                if (record.Reason == CsvMalformedReason.StrayQuote)
                {
                    strayQuoteRecords++;
                }
            }
        };

        var records = new List<string[]>();
        using var reader = CsvReader.FromText(text, options);
        try
        {
            while ((maxRecords is null || records.Count < maxRecords) && reader.Read())
            {
                var record = new string[reader.FieldCount];
                var broken = false;
                for (var i = 0; i < record.Length; i++)
                {
                    record[i] = reader.GetString(i);
                    broken |= HasBrokenQuoting(reader.GetRawSpan(i));
                }

                if (broken)
                {
                    if (throwOnBadData)
                    {
                        throw new InvalidDataException(
                            "Filen har et anførselstegn som ikke er lukket riktig. Sjekk at riktig fil og format er valgt.");
                    }

                    brokenRecords++;
                }

                if (record.Any(c => !string.IsNullOrWhiteSpace(c)))
                {
                    records.Add(record);
                }
            }
        }
        catch (CsvFormatException ex) when (throwOnBadData)
        {
            throw new InvalidDataException("Filen kunne ikke leses som CSV. Sjekk at riktig fil og format er valgt.", ex);
        }
        catch (CsvFormatException)
        {
            // Ved gjenkjenning av skilletegn regnes et lesebrudd som ødelagte data.
            brokenRecords++;
        }

        return (records, brokenRecords, strayQuoteRecords);
    }

    /// <summary>
    /// Om et sitert felt (rå bytes slik det står i filen) er ødelagt: det lukkes aldri, eller det følger tekst etter
    /// det avsluttende anførselstegnet. Begge deler betyr at feltet har slukt tekst det ikke skulle hatt, f.eks. når
    /// avsluttende «"» mangler i en fil der alle felt er sitert, slik at neste rad havner i feltet. «""» er et
    /// escapet anførselstegn. Uquotede felt med løse anførselstegn («12" panne») er ikke ødelagt.
    /// </summary>
    private static bool HasBrokenQuoting(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty || raw[0] != (byte)'"')
        {
            return false;
        }

        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'"')
            {
                continue;
            }

            if (i + 1 < raw.Length && raw[i + 1] == (byte)'"')
            {
                i++;
                continue;
            }

            return i != raw.Length - 1;
        }

        return true;
    }
}
