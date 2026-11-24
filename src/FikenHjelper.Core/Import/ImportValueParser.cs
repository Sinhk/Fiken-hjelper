using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FikenHjelper.Core.Import;

/// <summary>Tolking av celleverdier fra eksportfiler (ja/nei, beløp, heltall, nøkler).</summary>
public static partial class ImportValueParser
{
    private static readonly HashSet<string> TrueValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "j", "yes", "y", "true", "sann", "1", "x", "✓", "✔"
    };

    private static readonly HashSet<string> FalseValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "nei", "n", "no", "false", "usann", "0", "-", "–"
    };

    public static bool ParseBool(string? value)
        => !string.IsNullOrWhiteSpace(value) && TrueValues.Contains(value.Trim());

    /// <summary>
    /// Ja/nei-felt der også en annen utfylt verdi betyr ja. Min speiding fyller f.eks. «Bekreftet» med
    /// tidspunktet for bekreftelsen («2026-11-15 06:15»). Tomt, «-» og nei-verdier gir false.
    /// </summary>
    public static bool ParseFlag(string? value)
        => !string.IsNullOrWhiteSpace(value) && !FalseValues.Contains(value.Trim());

    /// <summary>
    /// Tolker beløpsceller som «450», «450,00», «1 250,50», «1.250,50», «kr 450», «450 kr», «450,-» eller «-450».
    /// Inneholder cellen annen tekst (f.eks. «Speider (450 kr)»), hentes beløpet som står inntil «kr»/«,-».
    /// Tekst uten slikt beløp (f.eks. «Betalt 2026») gir null.
    /// </summary>
    public static decimal? ParseAmountNok(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = NormalizeSpaces(value).Trim();
        var bare = CurrencyTokens().Replace(text, " ").Trim();
        if (bare.Length > 0 && !bare.Any(char.IsLetter))
        {
            return ParseNumber(bare);
        }

        return ExtractAmountFromLabel(text);
    }

    /// <summary>
    /// Henter beløp fra en tekst som «Speider (1.250 kr)», «kr 450 for ledere», «Lederkurs: 700.00»
    /// (Min speiding-format) eller «Gratis».
    /// </summary>
    public static decimal? ExtractAmountFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var text = NormalizeSpaces(label);
        var suffix = SuffixAmount().Match(text);
        if (suffix.Success && ParseNumber(suffix.Groups["n"].Value) is { } s)
        {
            return s;
        }

        var prefix = PrefixAmount().Match(text);
        if (prefix.Success && ParseNumber(prefix.Groups["n"].Value) is { } p)
        {
            return p;
        }

        var colon = ColonAmount().Match(text);
        if (colon.Success && ParseNumber(colon.Groups["n"].Value) is { } c)
        {
            return c;
        }

        return text.Contains("gratis", StringComparison.OrdinalIgnoreCase)
            || FreeWord().IsMatch(text)
                ? 0m
                : null;
    }

    /// <summary>Tolker et rent tall med norsk eller engelsk desimal-/tusenskille.</summary>
    public static decimal? ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = new string(NormalizeSpaces(value).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length == 0 || !s.All(c => char.IsDigit(c) || c is ',' or '.' or '-') || s.LastIndexOf('-') > 0)
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
               && d >= long.MinValue
               && d <= long.MaxValue
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

    private static string NormalizeSpaces(string value)
        => value.Replace('\u00A0', ' ').Replace('\u202F', ' ');

    // Valutamarkører som fjernes før et beløp tolkes som rent tall.
    [GeneratedRegex(@"(?i)\bkr\.?|\bnok\b|,-|:-|\.-")]
    private static partial Regex CurrencyTokens();

    // «450 kr», «1.250,50 kr», «1 250,-», «-450 kr». Tallet kan ikke starte midt i et annet tall.
    [GeneratedRegex(@"(?i)(?<![\d.,])(?<n>-?\d{1,3}(?:[ .]\d{3})+(?:,\d{1,2})?|-?\d+(?:[.,]\d{1,2})?)\s*(?:kr\b|nok\b|,-)")]
    private static partial Regex SuffixAmount();

    // «kr 450», «kr 1.250,50», «NOK 1 250».
    [GeneratedRegex(@"(?i)(?:\bkr\.?|\bnok)\s*(?<n>-?\d{1,3}(?:[ .]\d{3})+(?:,\d{1,2})?|-?\d+(?:[.,]\d{1,2})?)(?![\d])")]
    private static partial Regex PrefixAmount();

    // Min speiding eksporterer avgiften som «<Navn>: <beløp>», f.eks. «Lederkurs: 700.00».
    [GeneratedRegex(@":\s*(?<n>-?\d+(?:[.,]\d{1,2})?)\s*$")]
    private static partial Regex ColonAmount();

    [GeneratedRegex(@"(?i)\bfree\b")]
    private static partial Regex FreeWord();

    private static bool IsThousandsSeparated(string s, char separator)
    {
        // «1,250» / «12.500.000»: hver gruppe etter skilletegnet har nøyaktig tre sifre.
        var parts = s.TrimStart('-').Split(separator);
        return parts.Length > 1
            && parts[0].Length is >= 1 and <= 3
            && parts[0][0] != '0'
            && parts[0].All(char.IsDigit)
            && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsDigit));
    }
}
