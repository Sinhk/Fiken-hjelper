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

    private static InvoiceDraftPlanOptions GroupOptions(long? fikenProjectId = null, string? invoiceText = null, string orderReferencePrefix = "")
        => new(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal> { ["fee:101"] = 450m, ["fee:102"] = 250m, ["fee:103"] = 0m },
            ParticipantOverrides: new Dictionary<string, ParticipantOverride>(),
            ManualRecipientContactIds: new Dictionary<string, long>(),
            IncomeAccount: "3200",
            IssueDate: new DateOnly(2026, 11, 23),
            DaysUntilDueDate: 14,
            FikenProjectId: fikenProjectId,
            BankAccountNumber: null,
            InvoiceText: invoiceText,
            OurReference: null,
            OrderReferencePrefix: orderReferencePrefix);

    private static FikenInvoiceDraftSummary ExistingDraft(long customerId, long? projectId, string? invoiceText, string? orderReference = null)
        => new(
            DraftId: 1,
            Uuid: null,
            Type: "invoice",
            IssueDate: "2026-11-20",
            DaysUntilDueDate: 14,
            InvoiceText: invoiceText,
            OrderReference: orderReference,
            Net: 0,
            Gross: 0,
            ProjectId: projectId,
            Customers: [Customers.Single(c => c.ContactId == customerId)]);

    [Fact]
    public void Build_OrderReferenceIsOptionalAndLongDescriptionsAreTruncatedTo200Chars()
    {
        var eventData = DemoEventFactory.CreateDemoEvent() with { EventTitle = new string('A', 240) };

        var noPrefixOptions = GroupOptions();
        var noPrefixPlan = InvoicePlanBuilder.Build(eventData, noPrefixOptions, Customers, new Dictionary<string, long>());
        Assert.All(noPrefixPlan.Drafts, d =>
        {
            Assert.Equal(string.Empty, d.OrderReference);
            Assert.All(d.Lines, l => Assert.True(l.Description.Length <= 200));
        });
        var moldeNoPrefix = noPrefixPlan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Null(InvoicePlanBuilder.ToFikenRequest(moldeNoPrefix, noPrefixOptions).OrderReference);

        var withPrefixOptions = GroupOptions(orderReferencePrefix: "KBK26");
        var molde = InvoicePlanBuilder.Build(eventData, withPrefixOptions, Customers, new Dictionary<string, long>())
            .Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Equal("KBK26", molde.OrderReference);
        Assert.Equal("KBK26", InvoicePlanBuilder.ToFikenRequest(molde, withPrefixOptions).OrderReference);
    }

    [Fact]
    public void Build_FlagsDuplicateOnlyForSameCustomerAndSameEvent_RegardlessOfOrderReference()
    {
        var demo = DemoEventFactory.CreateDemoEvent();

        // With a Fiken project: same customer + same project is a duplicate, even with no order reference.
        var projectOptions = GroupOptions(fikenProjectId: 555, invoiceText: "Kretsleir");
        var existing = new List<FikenInvoiceDraftSummary>
        {
            ExistingDraft(1001, projectId: 555, invoiceText: "Noe annet"),
            ExistingDraft(1002, projectId: 999, invoiceText: "Kretsleir")
        };

        var plan = InvoicePlanBuilder.Build(demo, projectOptions, Customers, new Dictionary<string, long>(), existing);
        Assert.True(plan.Drafts.Single(d => d.SelectedContactId == 1001).DuplicateDraftExists);
        Assert.False(plan.Drafts.Single(d => d.SelectedContactId == 1002).DuplicateDraftExists);
        Assert.Equal(1, plan.DuplicateWarningCount);

        // A shared order reference no longer marks every draft as a duplicate.
        var sharedPrefixPlan = InvoicePlanBuilder.Build(
            demo,
            projectOptions with { OrderReferencePrefix = "KBK26" },
            Customers,
            new Dictionary<string, long>(),
            [ExistingDraft(1003, projectId: 999, invoiceText: null, orderReference: "KBK26")]);
        Assert.Equal(0, sharedPrefixPlan.DuplicateWarningCount);

        // Without a project: same customer + same invoice text is a duplicate.
        var noProjectPlan = InvoicePlanBuilder.Build(
            demo,
            GroupOptions(invoiceText: "Kretsleir"),
            Customers,
            new Dictionary<string, long>(),
            [ExistingDraft(1001, projectId: null, invoiceText: " kretsleir "), ExistingDraft(1002, projectId: null, invoiceText: "Vårleir")]);
        Assert.True(noProjectPlan.Drafts.Single(d => d.SelectedContactId == 1001).DuplicateDraftExists);
        Assert.False(noProjectPlan.Drafts.Single(d => d.SelectedContactId == 1002).DuplicateDraftExists);
    }

    [Fact]
    public void Build_ExcludeZeroPrice_KeepsParticipantsWithUnknownPriceAndWarns()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var participantsInFee101 = demo.Participants.Count(p => p.FeeCategoryKey == "fee:101" && !p.Cancelled);
        Assert.True(participantsInFee101 > 0);

        // fee:101 has no known price: not in the category prices, and nothing detected.
        var unknownPriceEvent = demo with
        {
            Participants = demo.Participants
                .Select(p => p.FeeCategoryKey == "fee:101" ? p with { DetectedPriceNok = null } : p)
                .ToList()
        };
        var options = GroupOptions() with
        {
            CategoryPricesNok = new Dictionary<string, decimal> { ["fee:102"] = 250m, ["fee:103"] = 0m },
            Filters = new ParticipantFilterOptions(ExcludeCancelled: true, ExcludeZeroPrice: true)
        };

        var plan = InvoicePlanBuilder.Build(unknownPriceEvent, options, Customers, new Dictionary<string, long>());

        var includedKeys = plan.Drafts.SelectMany(d => d.ParticipantKeys).ToHashSet();
        Assert.All(
            unknownPriceEvent.Participants.Where(p => p.FeeCategoryKey == "fee:101" && !p.Cancelled),
            p => Assert.Contains(p.ParticipantKey, includedKeys));
        Assert.Contains(plan.GlobalWarnings, w => w.Contains("mangler pris"));

        // Drafts with a missing price are held back, so they are not created as 0 kr invoices.
        var affectedKeys = unknownPriceEvent.Participants
            .Where(p => p.FeeCategoryKey == "fee:101" && !p.Cancelled)
            .Select(p => p.ParticipantKey)
            .ToHashSet();
        Assert.All(plan.Drafts, d =>
        {
            var affected = d.ParticipantKeys.Any(affectedKeys.Contains);
            Assert.Equal(affected, d.HasMissingPrice);
            if (affected)
            {
                Assert.False(d.IsReadyToCreate);
            }
        });

        // A participant override with a price clears the block for that participant.
        var pricedOverrides = affectedKeys.ToDictionary(k => k, _ => new ParticipantOverride(CustomPriceNok: 450m));
        var pricedPlan = InvoicePlanBuilder.Build(
            unknownPriceEvent,
            options with { ParticipantOverrides = pricedOverrides },
            Customers,
            new Dictionary<string, long>());
        Assert.DoesNotContain(pricedPlan.Drafts, d => d.HasMissingPrice);

        // Known 0 kr (fee:103) is still excluded.
        Assert.All(
            unknownPriceEvent.Participants.Where(p => p.FeeCategoryKey == "fee:103"),
            p => Assert.DoesNotContain(p.ParticipantKey, includedKeys));
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

        // A 0 kr draft is shown, but never created in Fiken, even when it has a customer.
        Assert.True(fraenaDraft.IsZeroAmount);
        Assert.False(fraenaDraft.IsReadyToCreate);
        Assert.Equal(1, zeroPricePlan.ZeroAmountDraftCount);
        Assert.Contains(zeroPricePlan.GlobalWarnings, w => w.Contains("0 kr og opprettes ikke"));
        Assert.DoesNotContain(zeroPricePlan.ReadyDrafts, d => d.DraftKey == fraenaDraft.DraftKey);

        var withCustomer = InvoicePlanBuilder.Build(
            demo,
            zeroPriceOptions with
            {
                ManualRecipientContactIds = new Dictionary<string, long> { [fraenaDraft.DraftKey] = 1003 }
            },
            Customers,
            new Dictionary<string, long>());
        var fraenaWithCustomer = Assert.Single(withCustomer.Drafts, d => d.DraftKey == fraenaDraft.DraftKey);
        Assert.Equal(1003, fraenaWithCustomer.SelectedContactId);
        Assert.False(fraenaWithCustomer.IsReadyToCreate);
        Assert.DoesNotContain(withCustomer.ReadyDrafts, d => d.DraftKey == fraenaDraft.DraftKey);
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

    [Fact]
    public void Build_CustomCategoryLabels_UsedOnInvoiceLinesAndDistrictCoveredSummary()
    {
        var demo = DemoEventFactory.CreateDemoEvent();
        var originalFee101Label = demo.FeeCategories.Single(c => c.Key == "fee:101").Label;
        var originalFee102Label = demo.FeeCategories.Single(c => c.Key == "fee:102").Label;

        var customLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fee:101"] = "Egen speideravgift",
            ["fee:102"] = "Egen lederavgift"
        };

        var baseOptions = new InvoiceDraftPlanOptions(
            BillingMode: BillingMode.PerScoutGroup,
            GroupLineMode: GroupLineMode.OneLinePerParticipant,
            Filters: new ParticipantFilterOptions(ExcludeCancelled: true),
            CategoryPricesNok: new Dictionary<string, decimal>
            {
                ["fee:101"] = 450m,
                ["fee:102"] = 250m
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
            OrderReferencePrefix: null,
            CategoryLabels: customLabels);

        // 1. PerScoutGroup + OneLinePerParticipant + DistrictCovered
        var oneLinePlan = InvoicePlanBuilder.Build(demo, baseOptions, Customers, new Dictionary<string, long>());
        var moldeOneLine = oneLinePlan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Contains(moldeOneLine.Lines, l => l.Description.Contains("Egen speideravgift", StringComparison.Ordinal));
        Assert.Contains(moldeOneLine.Lines, l => l.Description.Contains("Egen lederavgift", StringComparison.Ordinal));
        Assert.DoesNotContain(moldeOneLine.Lines, l => l.Description.Contains(originalFee101Label, StringComparison.Ordinal));
        Assert.DoesNotContain(moldeOneLine.Lines, l => l.Description.Contains(originalFee102Label, StringComparison.Ordinal));

        var covered = Assert.Single(oneLinePlan.DistrictCoveredParticipants);
        Assert.Equal("Egen lederavgift", covered.FeeLabel);
        var coveredSummary = InvoicePlanBuilder.FormatDistrictCoveredSummaryText(
            demo.EventTitle,
            oneLinePlan.DistrictCoveredParticipants);
        Assert.Contains("Egen lederavgift", coveredSummary, StringComparison.Ordinal);

        // 2. PerScoutGroup + SummaryPerFeeCategory
        var summaryPlan = InvoicePlanBuilder.Build(
            demo,
            baseOptions with { GroupLineMode = GroupLineMode.SummaryPerFeeCategory },
            Customers,
            new Dictionary<string, long>());
        var moldeSummary = summaryPlan.Drafts.Single(d => d.RecipientName == "Molde speidergruppe");
        Assert.Contains(moldeSummary.Lines, l => l.Description.StartsWith("Egen speideravgift", StringComparison.Ordinal));
        Assert.Contains(moldeSummary.Lines, l => l.Description.StartsWith("Egen lederavgift", StringComparison.Ordinal));

        // 3. PerParticipant
        var perParticipantPlan = InvoicePlanBuilder.Build(
            demo,
            baseOptions with { BillingMode = BillingMode.PerParticipant },
            Customers,
            new Dictionary<string, long>());
        Assert.Contains(
            perParticipantPlan.Drafts.SelectMany(d => d.Lines),
            l => l.Description.EndsWith("Egen speideravgift", StringComparison.Ordinal));
    }
}
