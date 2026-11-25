using System.Globalization;

namespace FikenHjelper.Tests;

/// <summary>
/// Forventede beløp i <c>[InlineData]</c> må skrives som double. Fra .NET 11 gir <c>(decimal)double</c> den
/// eksakte binærverdien (450,2 → 450,19999…), så de gjøres om via den korteste desimalteksten i stedet.
/// </summary>
internal static class TestAmounts
{
    public static decimal Exact(double value) =>
        decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
}
