using System.Globalization;

namespace FikenHjelper.Core.Invoicing;

/// <summary>
/// Gyldig område og tolking av «dager til forfall». Brukes av både Innstillinger og
/// fakturaoppsettet, så begge godtar det samme og viser samme melding.
/// </summary>
public static class DueDays
{
    public const int Min = 0;
    public const int Max = 365;

    public const string RangeMessage = "Forfallsfrist må være et helt tall mellom 0 og 365 dager.";

    /// <summary>
    /// Leser et helt tall i området <see cref="Min"/>–<see cref="Max"/>. Tom tekst, tekst som ikke er et
    /// helt tall og tall utenfor området gir <c>false</c>.
    /// </summary>
    public static bool TryParse(string? text, out int days)
    {
        days = 0;
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < Min or > Max)
        {
            return false;
        }

        days = parsed;
        return true;
    }
}
