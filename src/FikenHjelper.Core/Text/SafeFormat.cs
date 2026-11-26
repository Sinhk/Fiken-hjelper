using System.Globalization;

namespace FikenHjelper.Core.Text;

/// <summary>Formatering av tekstmaler som aldri kaster, f.eks. for oversatte etiketter.</summary>
public static class SafeFormat
{
    /// <summary>
    /// Setter inn <paramref name="arguments"/> i <paramref name="template"/>. Uten argumenter, eller når malen
    /// ikke passer med argumentene, returneres malen uendret i stedet for å kaste <see cref="FormatException"/>.
    /// </summary>
    public static string Format(string template, params object?[]? arguments)
    {
        if (arguments is null || arguments.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
