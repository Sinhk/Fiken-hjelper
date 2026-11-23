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

        // 10 participants in demo, 1 cancelled (Maja in 1. Kristiansund) => 9 included across 6 groups (5 scout groups + Uten gruppe)
        Assert.Equal(9, plan.IncludedParticipantCount);
        Assert.Equal(1, plan.ExcludedParticipantCount);
        Assert.Equal(6, plan.Drafts.Count);

        var moldeDraft = plan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Equal(4, moldeDraft.Lines.Count);
        Assert.Equal(115000, moldeDraft.TotalOre); // 450 + 450 + 250 + 0 = 1150 kr
        Assert.Equal(1001, moldeDraft.SelectedContactId);

        var fikenReq = InvoicePlanBuilder.ToFikenRequest(moldeDraft, options);
        Assert.Equal("invoice", fikenReq.Type);
        Assert.Equal(555, fikenReq.ProjectId);
        Assert.Equal("12345678903", fikenReq.BankAccountNumber);
        Assert.All(fikenReq.Lines, l =>
        {
            Assert.Equal("OUTSIDE", l.VatType);
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
        // Ola has custom 225.25, Ingrid has 450.50, Kari has 250, Anders Stab has 0 => 4 summary buckets
        Assert.Equal(4, molde.Lines.Count);
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

    [Fact]
    public void Build_ExcludesZeroPriceAndExcludedCategories_WithoutCreatingEmptyDrafts()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var baseOptions = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true, ExcludeZeroPrice: false),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450m,
                ["fee:102"] = 250m,
                ["fee:103"] = 0m
            },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>(),
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: null,
            OurReference: null,
            OrderReferencePrefix: "KBK26",
            ExcludedCategoryKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fee:103" });

        // Case A: exclude fee:103 (Stab) -> no empty draft for Fræna speidergruppe
        var categoryExcludedPlan = InvoicePlanBuilder.Build(demo, baseOptions, Customers, new Dictionary<string, long>());
        Assert.Equal(7, categoryExcludedPlan.IncludedParticipantCount);
        Assert.Equal(3, categoryExcludedPlan.ExcludedParticipantCount);
        Assert.Equal(5, categoryExcludedPlan.Drafts.Count);
        Assert.DoesNotContain(categoryExcludedPlan.Drafts, d => d.RecipientName == "Fræna speidergruppe");
        Assert.Equal(3, categoryExcludedPlan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe").Lines.Count);

        // Case B: ExcludeZeroPrice = true, with explicit Included = true override for Silje (90010)
        var zeroPriceOptions = baseOptions with
        {
            ExcludedCategoryKeys = null,
            Filters = new ParticipantFilterOptions(ExcludeCancelled: true, ExcludeZeroPrice: true),
            ParticipantOverrides = new Dictionary<string, ParticipantOverride>
            {
                ["90010"] = new ParticipantOverride(Included: true)
            }
        };

        var zeroPricePlan = InvoicePlanBuilder.Build(demo, zeroPriceOptions, Customers, new Dictionary<string, long>());
        Assert.Equal(8, zeroPricePlan.IncludedParticipantCount);
        Assert.Equal(2, zeroPricePlan.ExcludedParticipantCount);
        var fraenaDraft = Assert.Single(zeroPricePlan.Drafts, d => d.RecipientName == "Fræna speidergruppe");
        Assert.Equal(0, fraenaDraft.TotalOre);
    }

    [Fact]
    public void Build_CoveredByDistrict_RemovesFromGroupDraftsAndPopulatesSummary()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450m,
                ["fee:102"] = 250m,
                ["fee:103"] = 0m
            },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>
            {
                ["90007"] = new ParticipantOverride(CoveredByDistrict: true)
            },
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: null,
            OurReference: null,
            OrderReferencePrefix: "KBK26",
            ExcludedCategoryKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fee:103" });

        var plan = InvoicePlanBuilder.Build(demo, options, Customers, new Dictionary<string, long>());

        Assert.DoesNotContain(plan.Drafts, d => d.RecipientName == "Sunndal speidergruppe");
        Assert.Equal(6, plan.IncludedParticipantCount);
        Assert.Equal(3, plan.ExcludedParticipantCount);

        var covered = Assert.Single(plan.DistrictCoveredParticipants);
        Assert.Equal("90007", covered.ParticipantKey);
        Assert.Equal("Linn Berg", covered.FullName);
        Assert.Equal(25000, covered.AmountOre);
        Assert.Equal(25000, plan.DistrictCoveredTotalOre);

        var summary = InvoicePlanBuilder.FormatDistrictCoveredSummaryText(demo.EventTitle, plan.DistrictCoveredParticipants);
        Assert.Contains("Linn Berg", summary);
        Assert.Contains("Sunndal speidergruppe", summary);
        Assert.Contains("250,00 kr", summary);
    }

    [Fact]
    public void Build_CustomGroupOverride_MovesParticipantToSelectedGroupDraftAndDistrictSummary()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var options = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450m,
                ["fee:102"] = 250m,
                ["fee:103"] = 0m
            },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>
            {
                // Move Kari (90003, Molde -> Aukra) and sole member Linn (90007, Sunndal -> 1. Kristiansund)
                ["90003"] = new ParticipantOverride(CustomGroupName: "Aukra speidergruppe"),
                ["90007"] = new ParticipantOverride(CustomGroupName: "1. Kristiansund speidergruppe")
            },
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: null,
            BankAccountNumber: null,
            InvoiceText: null,
            OurReference: null,
            OrderReferencePrefix: "KBK26",
            ExcludedCategoryKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fee:103" });

        var plan = InvoicePlanBuilder.Build(demo, options, Customers, new Dictionary<string, long>());

        Assert.DoesNotContain(plan.Drafts, d => d.RecipientName == "Sunndal speidergruppe");

        var moldeDraft = plan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Equal(2, moldeDraft.Lines.Count);
        Assert.Equal(90000, moldeDraft.TotalOre);

        var aukraDraft = plan.Drafts.Single(d => d.RecipientName == "Aukra speidergruppe");
        Assert.Equal(2, aukraDraft.Lines.Count);
        Assert.Equal(70000, aukraDraft.TotalOre); // 450 (Sander) + 250 (Kari)
        Assert.Contains(aukraDraft.ParticipantKeys, k => k == "90003");

        var ksundDraft = plan.Drafts.Single(d => d.RecipientName == "1. Kristiansund speidergruppe");
        Assert.Equal(2, ksundDraft.Lines.Count);
        Assert.Equal(70000, ksundDraft.TotalOre); // 450 (Henrik) + 250 (Linn)

        // Combining CustomGroupName + CoveredByDistrict updates DistrictCoveredParticipant.GroupName
        var districtOptions = options with
        {
            ParticipantOverrides = new Dictionary<string, ParticipantOverride>
            {
                ["90003"] = new ParticipantOverride(CoveredByDistrict: true, CustomGroupName: "Aukra speidergruppe")
            }
        };

        var districtPlan = InvoicePlanBuilder.Build(demo, districtOptions, Customers, new Dictionary<string, long>());
        var aukraAfterDistrict = districtPlan.Drafts.Single(d => d.RecipientName == "Aukra speidergruppe");
        Assert.Single(aukraAfterDistrict.Lines);
        Assert.Equal(45000, aukraAfterDistrict.TotalOre);

        var covered = Assert.Single(districtPlan.DistrictCoveredParticipants);
        Assert.Equal("90003", covered.ParticipantKey);
        Assert.Equal("Aukra speidergruppe", covered.GroupName);
    }
}
