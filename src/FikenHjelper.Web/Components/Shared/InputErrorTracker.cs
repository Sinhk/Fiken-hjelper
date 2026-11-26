namespace FikenHjelper.Web.Components.Shared;

/// <summary>
/// Samler hvilke felt som har ugyldig tekst som ikke er tatt i bruk (f.eks. «12abc» i et tallfelt), så en side kan
/// blokkere lagring. Legges som <c>CascadingValue</c> rundt feltene. Feltene melder fra uten å vite om siden.
/// </summary>
public sealed class InputErrorTracker
{
    private readonly HashSet<object> _invalid = [];

    public bool HasErrors => _invalid.Count > 0;

    public void Report(object source, bool invalid)
    {
        if (invalid)
        {
            _invalid.Add(source);
        }
        else
        {
            _invalid.Remove(source);
        }
    }
}
