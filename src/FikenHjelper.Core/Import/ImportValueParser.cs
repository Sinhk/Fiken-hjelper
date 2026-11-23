using System.Globalization;
using System.Text;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Core.Import;

/// <summary>Tolking av celleverdier fra eksportfiler (ja/nei, beløp, heltall, nøkler).</summary>
public static class ImportValueParser
{
    private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "j", "yes", "y", "true", "sann", "1", "x", "✓", "✔"
    };

    public static bool ParseBool(string? value)
        => !string.IsNullOrWhiteSpace(value) && TrueValues.Contains(value.Trim());

    /// <summary>
    /// Tolker beløp som «450», «450,00», «1 250,50», «1.250,50», «kr 450» eller «450 kr».
    /// </summary>
    public static decimal? ParseAmountNok(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var fromLabel = MinSpeidingParser.TryExtractPriceNokFromLabel(text);
        if (fromLabel.HasValue)
        {
            return fromLabel.Value;
        }

        var cleaned = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch) || ch is ',' or '.' or '-')
            {
                cleaned.Append(ch);
            }
            else if (!char.IsWhiteSpace(ch) && ch != ' ' && !char.IsLetter(ch))
            {
                return null;
            }
        }

        var s = cleaned.ToString().TrimEnd('-');
        if (s.Length == 0 || s.Count(char.IsDigit) == 0)
        {
            return null;
        }

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Den siste av dem er desimalskilletegn.
            s = lastComma > lastDot
                ? s.Replace(".", string.Empty).Replace(',', '.')
                : s.Replace(",", string.Empty);
        }
        else if (lastComma >= 0)
        {
            // Norsk format: komma er desimalskilletegn, med mindre det er flere komma («1,250,000»).
            s = s.Count(c => c == ',') > 1 && IsThousandsSeparated(s, ',')
                ? s.Replace(",", string.Empty)
                : s.Replace(',', '.');
        }
        else if (lastDot >= 0 && IsThousandsSeparated(s, '.'))
        {
            s = s.Replace(".", string.Empty);
        }

        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    public static long? ParseLong(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = value.Trim();
        if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        // Excel kan gi «12345.0».
        return decimal.TryParse(digits.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
               && d == decimal.Truncate(d)
            ? (long)d
            : null;
    }

    /// <summary>Normaliserer en overskrift for synonymsøk: små bokstaver, æøå → ae/o/a, kun bokstaver/tall.</summary>
    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            switch (ch)
            {
                case 'æ':
                    sb.Append("ae");
                    break;
                case 'ø' or 'ö':
                    sb.Append('o');
                    break;
                case 'å' or 'ä':
                    sb.Append('a');
                    break;
                default:
                    if (char.IsLetterOrDigit(ch))
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Lager en filvennlig nøkkel som «kurshelg-hosten-2026».</summary>
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "uten-navn";
        }

        var sb = new StringBuilder();
        var lastWasDash = true;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            var mapped = ch switch
            {
                'æ' => "ae",
                'ø' or 'ö' => "o",
                'å' or 'ä' => "a",
                _ when char.IsAsciiLetterOrDigit(ch) => ch.ToString(),
                _ => null
            };

            if (mapped is null)
            {
                if (!lastWasDash)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }

                continue;
            }

            sb.Append(mapped);
            lastWasDash = false;
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "uten-navn" : slug;
    }

    private static bool IsThousandsSeparated(string s, char separator)
    {
        // «1,250» / «12.500.000»: hver gruppe etter skilletegnet har nøyaktig tre sifre.
        var parts = s.TrimStart('-').Split(separator);
        return parts.Length > 1
            && parts[0].Length is >= 1 and <= 3
            && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsDigit));
    }
}
