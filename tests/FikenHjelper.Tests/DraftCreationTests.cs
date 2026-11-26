using FikenHjelper.Core.Invoicing;

namespace FikenHjelper.Tests;

public sealed class DraftCreationTests
{
    [Fact]
    public void GetBlockedReason_AllowsConnectedCompanyWithRealData()
    {
        Assert.Null(DraftCreationGuard.GetBlockedReason(fikenConnected: true, companySlug: "testforetak-as", isDemoData: false));
    }

    [Fact]
    public void GetBlockedReason_BlocksDemoData_EvenWhenConnected()
    {
        Assert.Equal(
            DraftCreationGuard.DemoDataMessage,
            DraftCreationGuard.GetBlockedReason(fikenConnected: true, companySlug: "testforetak-as", isDemoData: true));
        Assert.Equal("Demodata kan ikke opprettes i Fiken.", DraftCreationGuard.DemoDataMessage);
    }

    [Fact]
    public void GetBlockedReason_BlocksWhenNotConnected()
    {
        var reason = DraftCreationGuard.GetBlockedReason(fikenConnected: false, companySlug: "testforetak-as", isDemoData: false);
        Assert.Equal(DraftCreationGuard.NotConnectedMessage, reason);
        Assert.Contains("Innstillinger", reason);
        Assert.DoesNotContain("API-nøkkel", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetBlockedReason_BlocksWithoutCompany(string? companySlug)
    {
        Assert.Equal(
            DraftCreationGuard.NoCompanyMessage,
            DraftCreationGuard.GetBlockedReason(fikenConnected: true, companySlug, isDemoData: false));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-100L, false)]
    [InlineData(1L, true)]
    [InlineData(45000L, true)]
    public void IsReadyToCreate_RequiresPositiveTotal(long totalOre, bool expectedReady)
    {
        var draft = Draft(totalOre, contactId: 1001);
        Assert.Equal(expectedReady, draft.IsReadyToCreate);
        Assert.Equal(!expectedReady, draft.IsZeroAmount);
    }

    [Fact]
    public void Plan_ReadyDraftsAndZeroAmountCount_ExcludeZeroAndMissingPrice()
    {
        var ready = Draft(45000, contactId: 1001, key: "group:a");
        var zero = Draft(0, contactId: 1002, key: "group:b");
        var missingPrice = Draft(0, contactId: 1003, key: "group:c", hasMissingPrice: true);
        var unmatched = Draft(25000, contactId: null, key: "group:d");

        var plan = new InvoicePlanResult(
            Drafts: [ready, zero, missingPrice, unmatched],
            IncludedParticipantCount: 4,
            ExcludedParticipantCount: 0,
            TotalOre: 70000,
            UnmatchedDraftCount: 1,
            DuplicateWarningCount: 0,
            GlobalWarnings: [],
            DistrictCoveredParticipants: [],
            DistrictCoveredTotalOre: 0);

        Assert.Equal(["group:a"], plan.ReadyDrafts.Select(d => d.DraftKey));
        Assert.Equal(450m, plan.ReadyTotalNok);
        // A missing price is reported on its own; it is not counted as a 0 kr draft.
        Assert.Equal(1, plan.ZeroAmountDraftCount);
    }

    private static PlannedInvoiceDraft Draft(long totalOre, long? contactId, string key = "group:test", bool hasMissingPrice = false)
        => new(
            DraftKey: key,
            RecipientKey: key,
            RecipientName: "Testgruppe speidergruppe",
            RecipientEmail: "test@example.no",
            OrderReference: "TEST-1",
            SelectedContactId: contactId,
            SuggestedContactId: null,
            MatchConfidence: CustomerMatchConfidence.ExactName,
            MatchReason: "Test",
            Lines: [new PlannedInvoiceLine("Deltaker 900001", 1, totalOre / 100m, totalOre, totalOre, null)],
            ParticipantKeys: ["900001"],
            TotalOre: totalOre,
            DuplicateDraftExists: false,
            Warnings: [],
            HasMissingPrice: hasMissingPrice);
}
