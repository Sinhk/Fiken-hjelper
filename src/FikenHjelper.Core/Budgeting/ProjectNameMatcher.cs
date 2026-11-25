using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Raffinert.FuzzySharp;

namespace FikenHjelper.Core.Budgeting;

/// <summary>Hvorfor et navn ble foreslått. Rekkefølgen er også prioriteten.</summary>
public enum NameMatchReason
{
    /// <summary>Samme navn etter normalisering.</summary>
    Exact,

    /// <summary>Var koblet sammen året før.</summary>
    History,

    /// <summary>Navnene ligner.</summary>
    Similar
}

/// <summary>Et treff med likhet 0–100.</summary>
public sealed record NameMatch<T>(T Item, int Score, NameMatchReason Reason);

/// <summary>
/// Likhetsmatching av prosjekt- og postnavn. Navnene normaliseres (små bokstaver, uten tegnsetting), og sammenlignes med <c>Fuzz.WeightedRatio</c> fra Raffinert.FuzzySharp,
/// som tåler skrivefeil, ulik ordrekkefølge og at det ene navnet står inni det andre.
/// </summary>
public static partial class ProjectNameMatcher
{
    /// <summary>Laveste likhet som vises som forslag.</summary>
    public const int SuggestionThreshold = 60;

    /// <summary>
    /// Laveste likhet for et sikkert forslag. Når det ene navnet står inni det andre («Kurs» og «Lederkurs»),
    /// gir WeightedRatio høyst 90. Slike treff vises, men velges ikke på forhånd.
    /// </summary>
    public const int StrongThreshold = 91;

    /// <summary>Et sikkert forslag må ligge minst så mye foran det nest beste.</summary>
    public const int StrongMargin = 5;

    private static readonly CultureInfo Nb = CultureInfo.GetCultureInfo("nb-NO");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>
    /// Gjør et navn klart for sammenligning, f.eks. «Landsleir (2026)» → «landsleir 2026». Årstall beholdes,
    /// siden kretsen kan ha ett prosjekt per år med ellers samme navn.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLower(Nb))
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var normalized = Whitespace().Replace(builder.ToString(), " ").Trim();

        // Består navnet bare av tegn, er det bedre å sammenligne det som det er.
        return normalized.Length > 0 ? normalized : name.Trim().ToLower(Nb);
    }

    /// <summary>Likhet mellom to navn, 0–100. 100 betyr samme navn etter normalisering.</summary>
    public static int Score(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        return x == y ? 100 : Math.Min(99, (int)Math.Round(Fuzz.WeightedRatio(x, y)));
    }

    /// <summary>
    /// Rangerer kandidater etter likhet med <paramref name="name"/>: like navn først, så kandidater fra
    /// fjorårets kobling, så de mest like. Kandidater under <paramref name="minScore"/> tas bare med
    /// når de er like eller fra historikken.
    /// </summary>
    public static IReadOnlyList<NameMatch<T>> Rank<T>(
        string? name,
        IEnumerable<T> candidates,
        Func<T, string?> nameOf,
        Func<T, bool>? isHistory = null,
        int minScore = SuggestionThreshold)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(nameOf);

        var comparer = StringComparer.Create(Nb, ignoreCase: true);
        return candidates
            .Select(c =>
            {
                var score = Score(name, nameOf(c));
                var reason = score == 100
                    ? NameMatchReason.Exact
                    : isHistory?.Invoke(c) == true ? NameMatchReason.History : NameMatchReason.Similar;
                return new NameMatch<T>(c, score, reason);
            })
            .Where(m => m.Reason != NameMatchReason.Similar || m.Score >= minScore)
            .OrderBy(m => m.Reason)
            .ThenByDescending(m => m.Score)
            .ThenBy(m => nameOf(m.Item), comparer)
            .ToList();
    }

    /// <summary>
    /// Om det beste treffet er sikkert nok til å velges på forhånd: likt navn eller fjorårets kobling,
    /// eller høy likhet med klar avstand til det nest beste.
    /// </summary>
    public static bool IsStrong<T>(IReadOnlyList<NameMatch<T>> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Count == 0)
        {
            return false;
        }

        var best = ranked[0];
        var runnerUp = ranked.Count > 1 ? ranked[1] : null;
        if (best.Reason != NameMatchReason.Similar)
        {
            // To like gode treff av samme slag er ikke sikkert.
            return runnerUp is null || runnerUp.Reason != best.Reason;
        }

        return best.Score >= StrongThreshold
            && (runnerUp is null || best.Score - runnerUp.Score >= StrongMargin);
    }
}
