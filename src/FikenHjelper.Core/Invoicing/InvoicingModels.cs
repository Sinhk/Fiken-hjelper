using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Core.Invoicing;

public enum BillingMode
{
    PerScoutGroup,
    PerParticipant
}

public enum GroupLineMode
{
    OneLinePerParticipant,
    SummaryPerFeeCategory
}

public sealed record ParticipantFilterOptions(
    bool ExcludeCancelled = true,
    bool RequireConfirmed = false,
    bool RequireAttended = false,
    bool ExcludeZeroPrice = false);

public sealed record ParticipantOverride(
    bool? Included = null,
    decimal? CustomPriceNok = null,
    bool CoveredByDistrict = false);

public sealed record InvoiceDraftPlanOptions(
    BillingMode BillingMode,
    GroupLineMode GroupLineMode,
    ParticipantFilterOptions Filters,
    IReadOnlyDictionary<string, decimal> CategoryPricesNok,
    IReadOnlyDictionary<string, ParticipantOverride> ParticipantOverrides,
    IReadOnlyDictionary<string, long> ManualRecipientContactIds,
    string IncomeAccount,
    DateOnly IssueDate,
    int DaysUntilDueDate,
    long? FikenProjectId,
    string? BankAccountNumber,
    string? InvoiceText,
    string? OurReference,
    string? OrderReferencePrefix,
    IReadOnlySet<string>? ExcludedCategoryKeys = null);

public sealed record DistrictCoveredParticipant(
    string ParticipantKey,
    string FullName,
    long? MemberNo,
    string GroupName,
    string FeeLabel,
    decimal AmountNok,
    long AmountOre);

public sealed record PlannedInvoiceLine(
    string Description,
    decimal Quantity,
    decimal UnitPriceNok,
    long UnitPriceOre,
    long LineTotalOre,
    string? Comment);

public sealed record PlannedInvoiceDraft(
    string DraftKey,
    string RecipientKey,
    string RecipientName,
    string? RecipientEmail,
    string OrderReference,
    long? SelectedContactId,
    long? SuggestedContactId,
    CustomerMatchConfidence MatchConfidence,
    string MatchReason,
    IReadOnlyList<PlannedInvoiceLine> Lines,
    IReadOnlyList<string> ParticipantKeys,
    long TotalOre,
    bool DuplicateOrderReferenceExists,
    IReadOnlyList<string> Warnings)
{
    public decimal TotalNok => TotalOre / 100m;
    public bool IsReadyToCreate => SelectedContactId.HasValue && Lines.Count > 0;
}

public sealed record InvoicePlanResult(
    IReadOnlyList<PlannedInvoiceDraft> Drafts,
    int IncludedParticipantCount,
    int ExcludedParticipantCount,
    long TotalOre,
    int UnmatchedDraftCount,
    int DuplicateWarningCount,
    IReadOnlyList<string> GlobalWarnings,
    IReadOnlyList<DistrictCoveredParticipant> DistrictCoveredParticipants,
    long DistrictCoveredTotalOre)
{
    public decimal TotalNok => TotalOre / 100m;
    public decimal DistrictCoveredTotalNok => DistrictCoveredTotalOre / 100m;
}

public sealed record DraftCreationItemResult(
    string DraftKey,
    string RecipientName,
    bool Success,
    long? CreatedDraftId,
    string? ErrorMessage);

public static class CurrencyHelper
{
    public static long ToOre(decimal nokAmount)
        => (long)Math.Round(nokAmount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromOre(long ore)
        => ore / 100m;
}
