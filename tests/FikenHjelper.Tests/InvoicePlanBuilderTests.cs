using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.Invoicing;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Tests;

public sealed class InvoicePlanBuilderTests
{
    private static readonly List<FikenContact> Customers =
    [
        new(1001, "Molde speidergruppe", "molde@example.no", null, 1, null, true, false, false, 14, null),
        new(1002, "1. Kristiansund speidergruppe", "1ksund@example.no", null, 2, null, true, false, false, 14, null),
        new(1003, "Aukra", "aukra@example.no", null, 3, null, true, false, false, 14, null)
    ];

    [Fact]
    public void Build_PerScoutGroup_OneLinePerParticipant_GroupsAndExcludesCancelled()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450m,
                ["fee:102"] = 250m
            },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>(),
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: 555,
            BankAccountNumber: "12345678903",
            InvoiceText: "Kretsbannerkonkurranse",
            OurReference: "Kasserer",
            OrderReferencePrefix: "KBK26");

        var plan = InvoicePlanBuilder.Build(
            demo,
            options,
            Customers,
            new Dictionary<string, long>());

        // 8 participants in demo, 1 cancelled (Maja in 1. Kristiansund) => 7 included across 5 groups (4 scout groups + Uten gruppe)
        Assert.Equal(7, plan.IncludedParticipantCount);
        Assert.Equal(1, plan.ExcludedParticipantCount);
        Assert.Equal(5, plan.Drafts.Count);

        var moldeDraft = plan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Equal(3, moldeDraft.Lines.Count);
        Assert.Equal(115000, moldeDraft.TotalOre); // 450 + 450 + 250 = 1150 kr
        Assert.Equal(1001, moldeDraft.SelectedContactId);

        var fikenReq = InvoicePlanBuilder.ToFikenRequest(moldeDraft, options);
        Assert.Equal("invoice", fikenReq.Type);
        Assert.Equal(555, fikenReq.ProjectId);
        Assert.Equal("12345678903", fikenReq.BankAccountNumber);
        Assert.All(fikenReq.Lines, l =>
        {
            Assert.Equal("NONE", l.VatType);
            Assert.Equal("3200", l.IncomeAccount);
        });
    }

    [Fact]
    public void Build_SummaryPerFeeCategory_RespectsParticipantOverridePricesAndRoundsOreCorrectly()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.SummaryPerFeeCategory,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450.50m,
                ["fee:102"] = 250m
            },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>
            {
                ["90001"] = new ParticipantOverride(Included: true, CustomPriceNok: 225.25m)
            },
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: null,
            OurReference: null,
            OrderReferencePrefix: "KBK26");

        var plan = InvoicePlanBuilder.Build(demo, options, Customers, new Dictionary<string, long>());

        var molde = plan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        // Ola has custom 225.25, Ingrid has 450.50, Kari has 250 => 3 summary buckets!
        Assert.Equal(3, molde.Lines.Count);
        Assert.Equal(22525 + 45050 + 25000, molde.TotalOre);
    }

    [Fact]
    public void Build_DetectsDuplicateOrderReferenceAndTruncatesLongDescriptionsTo200Chars()
    {
        var longEventName = new string('A', 240);
        var eventData = DemoEventFactory.CreateDemoEvent() with { EventTitle = longEventName };

        var expectedOrderRef = InvoicePlanBuilder.BuildOrderReference("KBK26", eventData.ProjectId, "Molde speidergruppe");
        var existingRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expectedOrderRef };

        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerParticipant,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal> { ["fee:101"] = 450m, ["fee:102"] = 250m },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>(),
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: null,
            OurReference: null,
            OrderReferencePrefix: "KBK26");

        var plan = InvoicePlanBuilder.Build(
            eventData,
            options,
            Customers,
            new Dictionary<string, long>(),
            existingRefs);

        Assert.All(plan.Drafts, d => Assert.All(d.Lines, l => Assert.True(l.Description.Length <= 200)));

        // Switch to PerScoutGroup to verify duplicate detection on Molde speidergruppe
        var groupPlan = InvoicePlanBuilder.Build(
            eventData,
            options with { BillingMode = BillingMode.PerScoutGroup },
            Customers,
            new Dictionary<string, long>(),
            existingRefs);

        var molde = groupPlan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.True(molde.DuplicateOrderReferenceExists);
        Assert.Equal(1, groupPlan.DuplicateWarningCount);
    }
}
