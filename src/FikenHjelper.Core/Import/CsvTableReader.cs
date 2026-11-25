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
    /// Leser ikke-tomme rader. Teller rader med ødelagt sitering (et sitert felt med tekst etter avsluttende
    /// anførselstegn, eller som aldri lukkes) og rader med løse anførselstegn i uquotede felt hver for seg.
    /// </summary>
    private static (List<string[]> Records, int BrokenRecords, int StrayQuoteRecords) ReadRecords(
        string text, string delimiter, int? maxRecords, bool throwOnBadData)
    {
        var unclosedQuote = false;
        var brokenRecords = 0;
        var strayQuoteRecords = 0;
        var options = new CsvReaderOptions
        {
            Separator = delimiter[0],
            HasHeader = false,
            SkipEmptyLines = true,
            // Et anførselstegn midt i et uquotet felt (f.eks. «12" panne») er vanlig tekst, og «"ab"c» leses som
            // «abc». Et sitert felt som aldri lukkes, sluker derimot resten av filen, så det skal gi feil i stedet
            // for stille datatap (se IsUnterminatedQuotedField).
            MalformedData = CsvMalformedDataHandling.Recover,
            // CsvHelper melder bare den første feilen i hver rad.
            OnMalformedRecord = record =>
            {
                if (record.Reason == CsvMalformedReason.StrayQuote)
                {
                    strayQuoteRecords++;
                }
                else
                {
                    brokenRecords++;
                }

                unclosedQuote |= record.Reason == CsvMalformedReason.UnterminatedQuote;
            }
        };

        var records = new List<string[]>();
        using var reader = CsvReader.FromText(text, options);
        try
        {
            while ((maxRecords is null || records.Count < maxRecords) && reader.Read())
            {
                var record = new string[reader.FieldCount];
                for (var i = 0; i < record.Length; i++)
                {
                    record[i] = reader.GetString(i);
                }

                if (record.Length > 0 && IsUnterminatedQuotedField(reader.GetRawSpan(record.Length - 1)))
                {
                    if (!unclosedQuote)
                    {
                        // Skjult bak en tidligere feil i samme rad, f.eks. et løst anførselstegn.
                        brokenRecords++;
                    }

                    unclosedQuote = true;
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

        if (unclosedQuote && throwOnBadData)
        {
            throw new InvalidDataException("Filen har et anførselstegn som ikke er lukket. Sjekk at riktig fil og format er valgt.");
        }

        return (records, brokenRecords, strayQuoteRecords);
    }

    /// <summary>
    /// Om et felt (rå bytes slik det står i filen) starter med anførselstegn uten at det lukkes igjen. Et slikt felt
    /// sluker resten av filen og blir alltid det siste. CsvHelper melder bare første feil per rad, så denne sjekken
    /// fanger også tilfeller der et løst anførselstegn tidligere i raden skjuler det. «""» er et escapet anførselstegn.
    /// </summary>
    private static bool IsUnterminatedQuotedField(ReadOnlySpan<byte> raw)
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

            return false;
        }

        return true;
    }
}
