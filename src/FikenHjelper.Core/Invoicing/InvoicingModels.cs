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
    bool CoveredByDistrict = false,
    string? CustomGroupName = null);

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
    IReadOnlySet<string>? ExcludedCategoryKeys = null,
    IReadOnlyDictionary<string, string>? CategoryLabels = null);

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
    bool DuplicateDraftExists,
    IReadOnlyList<string> Warnings,
    bool HasMissingPrice = false)
{
    public decimal TotalNok => TotalOre / 100m;

    /// <summary>
    /// The draft sums to exactly 0 kr (e.g. a group whose only participant is staff at 0 kr).
    /// Such drafts are shown in the preview but not created in Fiken.
    /// </summary>
    public bool IsZeroAmount => TotalOre == 0;

    /// <summary>
    /// The draft sums to less than 0 kr (e.g. a negative custom price). It is not created in Fiken,
    /// and is labelled separately from 0 kr drafts.
    /// </summary>
    public bool IsNegativeAmount => TotalOre < 0;

    // A draft with a participant whose price is unknown would be created as a 0 kr line,
    // so it is held back until a price is set or the category is excluded. A draft that already
    // exists in Fiken for this event is held back too, so a second run does not create duplicates.
    public bool IsReadyToCreate => SelectedContactId.HasValue
        && Lines.Count > 0
        && !HasMissingPrice
        && !IsZeroAmount
        && !IsNegativeAmount
        && !DuplicateDraftExists;
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

    /// <summary>Drafts that would be created in Fiken right now.</summary>
    public IReadOnlyList<PlannedInvoiceDraft> ReadyDrafts => Drafts.Where(d => d.IsReadyToCreate).ToList();

    public decimal ReadyTotalNok => Drafts.Where(d => d.IsReadyToCreate).Sum(d => d.TotalOre) / 100m;

    /// <summary>Drafts with a known total of 0 kr; they are not created in Fiken.</summary>
    public int ZeroAmountDraftCount => Drafts.Count(d => d.IsZeroAmount && !d.HasMissingPrice);

    /// <summary>Drafts with a known negative total; they are not created in Fiken.</summary>
    public int NegativeAmountDraftCount => Drafts.Count(d => d.IsNegativeAmount && !d.HasMissingPrice);

    /// <summary>Drafts that already exist as drafts in Fiken; they are not created again.</summary>
    public int DuplicateDraftCount => Drafts.Count(d => d.DuplicateDraftExists);
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
