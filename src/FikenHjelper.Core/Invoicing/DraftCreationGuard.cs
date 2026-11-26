namespace FikenHjelper.Core.Invoicing;

/// <summary>
/// Decides whether invoice drafts may be created in Fiken at all. The UI uses it to disable the
/// create button, and the page checks it again before calling Fiken.
/// </summary>
public static class DraftCreationGuard
{
    public const string NotConnectedMessage =
        "Fiken er ikke tilkoblet. Logg inn med Fiken (OAuth) under Innstillinger før du oppretter utkast.";

    public const string NoCompanyMessage =
        "Velg Fiken-foretak øverst på siden før du oppretter utkast.";

    public const string DemoDataMessage =
        "Demodata kan ikke opprettes i Fiken.";

    /// <summary>
    /// Returns a Norwegian reason why drafts cannot be created, or <c>null</c> when creation is allowed.
    /// </summary>
    public static string? GetBlockedReason(bool fikenConnected, string? companySlug, bool isDemoData)
    {
        if (isDemoData)
        {
            return DemoDataMessage;
        }

        if (!fikenConnected)
        {
            return NotConnectedMessage;
        }

        return string.IsNullOrWhiteSpace(companySlug) ? NoCompanyMessage : null;
    }
}
