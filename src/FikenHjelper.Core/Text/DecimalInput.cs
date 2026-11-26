using System.Globalization;

namespace FikenHjelper.Core.Text;

/// <summary>
/// Tolker tall som brukere skriver inn i skjemafelt. Både «.» og «,» godtas som desimalskilletegn,
/// uavhengig av nettleserens og maskinens språkinnstillinger. Ingen av dem godtas som tusenskilletegn,
/// men mellomrom (også hardt og smalt mellomrom) ignoreres slik at «1 234,50» fungerer.
/// </summary>
public static class DecimalInput
{
    public const string InvalidMessage = "Skriv et tall, f.eks. 350,50. Bruk komma eller punktum som desimaltegn.";

    // Vanlig, hardt, smalt hardt og tynt mellomrom. Norsk formatering bruker hardt mellomrom som tusenskille.
    private static readonly char[] Spaces = [' ', ' ', ' ', ' ', ' '];

    /// <summary>
    /// Leser et desimaltall. Tom tekst, tekst med bokstaver eller andre tegn, og tekst med både «.» og «,»
    /// eller flere skilletegn gir <c>false</c>. Et ledende «-» eller «+» godtas, likeså et avsluttende
    /// skilletegn («12,») slik at tallet kan leses mens brukeren fortsatt skriver.
    /// </summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (text is null)
        {
            return false;
        }

        var normalized = Normalize(text);
        if (normalized is null)
        {
            return false;
        }

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Tom (eller bare mellomrom) tekst regnes som «ikke fylt ut», ikke som ugyldig.</summary>
    public static bool IsEmpty(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Formaterer <paramref name="value"/> for visning i et felt, med komma som desimaltegn. Med
    /// <paramref name="grouped"/> får store tall hardt mellomrom som tusenskille («150 000»), som
    /// <see cref="TryParse"/> leser tilbake.
    /// </summary>
    public static string Format(decimal value, int maxDecimals = 2, bool grouped = false)
        => Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero)
            .ToString((grouped ? "#,##0." : "0.") + new string('#', maxDecimals), CultureInfo.GetCultureInfo("nb-NO"))
            .Replace('\u2212', '-'); // nb-NO bruker det typografiske minustegnet, men i et felt skal man kunne skrive det på tastaturet

    /// <summary>Fjerner mellomrom og bytter «,» med «.». Returnerer <c>null</c> når formen ikke er et enkelt tall.</summary>
    private static string? Normalize(string text)
    {
        Span<char> buffer = stackalloc char[Math.Min(text.Length, 64)];
        var length = 0;
        var separators = 0;
        var digits = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (Array.IndexOf(Spaces, c) >= 0)
            {
                continue;
            }

            if (length == buffer.Length)
            {
                return null;
            }

            if (c is '.' or ',')
            {
                if (++separators > 1)
                {
                    return null;
                }

                buffer[length++] = '.';
            }
            else if (c is >= '0' and <= '9')
            {
                digits++;
                buffer[length++] = c;
            }
            else if (c is '-' or '+' or '−' && length == 0)
            {
                buffer[length++] = c == '−' ? '-' : c;
            }
            else
            {
                return null;
            }
        }

        return digits == 0 ? null : new string(buffer[..length]);
    }
}
