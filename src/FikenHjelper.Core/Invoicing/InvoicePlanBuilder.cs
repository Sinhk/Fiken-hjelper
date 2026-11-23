using System.Globalization;
using System.Text;
using FikenHjelper.Core.Fiken;
using FikenHjelper.Core.MinSpeiding;

namespace FikenHjelper.Core.Invoicing;

public static partial class InvoicePlanBuilder
{
    private const int MaxFikenLineFieldLength = 200;
    private const int MaxOrderReferenceLength = 100;
    private static readonly CultureInfo NorwegianCulture = CultureInfo.GetCultureInfo("nb-NO");

    public static bool IsParticipantIncluded(
        EventParticipant participant,
        ParticipantFilterOptions filters,
        IReadOnlyDictionary<string, ParticipantOverride> overrides)
        => IsParticipantIncluded(
            participant,
            filters,
            new Dictionary<string, decimal>(),
            overrides,
            excludedCategoryKeys: null);

    public static bool IsParticipantIncluded(
        EventParticipant participant,
        ParticipantFilterOptions filters,
        IReadOnlyDictionary<string, decimal> categoryPricesNok,
        IReadOnlyDictionary<string, ParticipantOverride> overrides,
        IReadOnlySet<string>? excludedCategoryKeys = null)
    {
        if (overrides.TryGetValue(participant.ParticipantKey, out var ov) && ov.Included.HasValue)
        {
            return ov.Included.Value;
        }

        if (excludedCategoryKeys is not null && excludedCategoryKeys.Contains(participant.FeeCategoryKey))
        {
            return false;
        }

        if (filters.ExcludeCancelled && participant.Cancelled)
        {
            return false;
        }

        if (filters.RequireConfirmed && !participant.Confirmed)
        {
            return false;
        }

        if (filters.RequireAttended && !participant.Attended && !participant.CheckedIn)
        {
            return false;
        }

        if (filters.ExcludeZeroPrice && ResolveEffectivePriceNok(participant, categoryPricesNok, overrides) <= 0m)
        {
            return false;
        }

        return true;
    }

    public static bool IsCoveredByDistrict(
        EventParticipant participant,
        IReadOnlyDictionary<string, ParticipantOverride> overrides)
        => overrides.TryGetValue(participant.ParticipantKey, out var ov) && ov.CoveredByDistrict;

    public static string ResolveEffectiveGroupName(
        EventParticipant participant,
        IReadOnlyDictionary<string, ParticipantOverride> overrides)
    {
        if (overrides.TryGetValue(participant.ParticipantKey, out var ov)
            && !string.IsNullOrWhiteSpace(ov.CustomGroupName))
        {
            return ov.CustomGroupName.Trim();
        }

        return participant.GroupName;
    }

    public static decimal ResolveEffectivePriceNok(
        EventParticipant participant,
        IReadOnlyDictionary<string, decimal> categoryPricesNok,
        IReadOnlyDictionary<string, ParticipantOverride> overrides)
    {
        if (overrides.TryGetValue(participant.ParticipantKey, out var ov) && ov.CustomPriceNok.HasValue)
        {
            return Math.Max(0m, ov.CustomPriceNok.Value);
        }

        if (categoryPricesNok.TryGetValue(participant.FeeCategoryKey, out var categoryPrice))
        {
            return Math.Max(0m, categoryPrice);
        }

        return Math.Max(0m, participant.DetectedPriceNok ?? 0m);
    }

    public static InvoicePlanResult Build(
        MinSpeidingEventLoadResult eventData,
        InvoiceDraftPlanOptions options,
        IReadOnlyList<FikenContact> fikenCustomers,
        IReadOnlyDictionary<string, long> savedGroupMappings,
        IReadOnlySet<string>? existingDraftOrderReferences = null)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fikenCustomers);
        ArgumentNullException.ThrowIfNull(savedGroupMappings);

        var billableParticipants = new List<EventParticipant>();
        var districtCoveredParticipants = new List<DistrictCoveredParticipant>();
        var excludedCount = 0;

        foreach (var participant in eventData.Participants)
        {
            if (!IsParticipantIncluded(
                    participant,
                    options.Filters,
                    options.CategoryPricesNok,
                    options.ParticipantOverrides,
                    options.ExcludedCategoryKeys))
            {
                excludedCount++;
                continue;
            }

            if (IsCoveredByDistrict(participant, options.ParticipantOverrides))
            {
                var amountNok = ResolveEffectivePriceNok(
                    participant,
                    options.CategoryPricesNok,
                    options.ParticipantOverrides);
                var amountOre = CurrencyHelper.ToOre(amountNok);
                var displayName = string.IsNullOrWhiteSpace(participant.FullName)
                    ? $"Deltaker {participant.ParticipantKey}"
                    : participant.FullName;

                districtCoveredParticipants.Add(new DistrictCoveredParticipant(
                    ParticipantKey: participant.ParticipantKey,
                    FullName: displayName,
                    MemberNo: participant.MemberNo,
                    GroupName: ResolveEffectiveGroupName(participant, options.ParticipantOverrides),
                    FeeLabel: participant.FeeLabel,
                    AmountNok: amountNok,
                    AmountOre: amountOre));
                continue;
            }

            billableParticipants.Add(participant);
        }

        var drafts = options.BillingMode == BillingMode.PerScoutGroup
            ? BuildPerGroupDrafts(eventData, billableParticipants, options, fikenCustomers, savedGroupMappings, existingDraftOrderReferences)
            : BuildPerParticipantDrafts(eventData, billableParticipants, options, fikenCustomers, savedGroupMappings, existingDraftOrderReferences);

        var totalOre = drafts.Sum(d => d.TotalOre);
        var districtCoveredTotalOre = districtCoveredParticipants.Sum(d => d.AmountOre);
        var unmatchedCount = drafts.Count(d => !d.SelectedContactId.HasValue);
        var duplicateCount = drafts.Count(d => d.DuplicateOrderReferenceExists);

        var globalWarnings = new List<string>();
        if (string.IsNullOrWhiteSpace(options.IncomeAccount))
        {
            globalWarnings.Add("Inntektskonto (f.eks. 3200) må velges før utkast kan opprettes i Fiken.");
        }

        if (unmatchedCount > 0)
        {
            globalWarnings.Add($"{unmatchedCount} mottaker(e) mangler kobling mot kunde i Fiken.");
        }

        if (duplicateCount > 0)
        {
            globalWarnings.Add($"{duplicateCount} utkast har samme ordrenummer som et eksisterende fakturautkast i Fiken.");
        }

        return new InvoicePlanResult(
            Drafts: drafts,
            IncludedParticipantCount: billableParticipants.Count,
            ExcludedParticipantCount: excludedCount,
            TotalOre: totalOre,
            UnmatchedDraftCount: unmatchedCount,
            DuplicateWarningCount: duplicateCount,
            GlobalWarnings: globalWarnings,
            DistrictCoveredParticipants: districtCoveredParticipants,
            DistrictCoveredTotalOre: districtCoveredTotalOre);
    }

    public static string FormatDistrictCoveredSummaryText(
        string eventTitle,
        IReadOnlyList<DistrictCoveredParticipant> covered)
    {
        if (covered.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Dekkes av kretsen – {eventTitle}");
        sb.AppendLine($"Antall deltakere: {covered.Count}");
        sb.AppendLine(new string('-', 44));
        foreach (var item in covered)
        {
            var memberPart = item.MemberNo.HasValue ? $" (medl.nr {item.MemberNo.Value})" : string.Empty;
            sb.AppendLine($"- {item.FullName}{memberPart}, {item.GroupName} – {item.FeeLabel}: {item.AmountNok.ToString("N2", NorwegianCulture)} kr");
        }

        var totalNok = CurrencyHelper.FromOre(covered.Sum(c => c.AmountOre));
        sb.AppendLine(new string('-', 44));
        sb.Append($"Sum som dekkes av kretsen: {totalNok.ToString("N2", NorwegianCulture)} kr");
        return sb.ToString();
    }

    public static FikenInvoiceDraftRequest ToFikenRequest(
        PlannedInvoiceDraft draft,
        InvoiceDraftPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(options);

        if (!draft.SelectedContactId.HasValue)
        {
            throw new InvalidOperationException(
                $"Kan ikke lage Fiken-forespørsel for '{draft.RecipientName}' uten valgt kunde-ID.");
        }

        var incomeAccount = string.IsNullOrWhiteSpace(options.IncomeAccount)
            ? "3200"
            : options.IncomeAccount.Trim();

        var fikenLines = draft.Lines
            .Select(line => new FikenInvoiceDraftLineRequest(
                Description: Truncate(line.Description, MaxFikenLineFieldLength),
                UnitPrice: line.UnitPriceOre,
                VatType: "OUTSIDE",
                Quantity: line.Quantity,
                IncomeAccount: incomeAccount,
                Comment: TruncateOrNull(line.Comment, MaxFikenLineFieldLength)))
            .ToList();

        return new FikenInvoiceDraftRequest(
            Type: "invoice",
            IssueDate: options.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DaysUntilDueDate: Math.Max(0, options.DaysUntilDueDate),
            InvoiceText: NullIfWhiteSpace(options.InvoiceText),
            YourReference: null,
            OurReference: NullIfWhiteSpace(options.OurReference),
            OrderReference: TruncateOrNull(draft.OrderReference, MaxOrderReferenceLength),
            Lines: fikenLines,
            Currency: "NOK",
            BankAccountNumber: NullIfWhiteSpace(options.BankAccountNumber),
            CustomerId: draft.SelectedContactId.Value,
            ProjectId: options.FikenProjectId);
    }

    public static string BuildOrderReference(string? prefix, string projectId, string key)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        return Truncate(prefix.Trim(), MaxOrderReferenceLength);
    }

    private static List<PlannedInvoiceDraft> BuildPerGroupDrafts(
        MinSpeidingEventLoadResult eventData,
        List<EventParticipant> participants,
        InvoiceDraftPlanOptions options,
        IReadOnlyList<FikenContact> fikenCustomers,
        IReadOnlyDictionary<string, long> savedGroupMappings,
        IReadOnlySet<string>? existingDraftOrderReferences)
    {
        var drafts = new List<PlannedInvoiceDraft>();

        var groups = participants
            .GroupBy(p => ResolveEffectiveGroupName(p, options.ParticipantOverrides), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Create(NorwegianCulture, ignoreCase: true));

        foreach (var group in groups)
        {
            var groupName = group.Key;
            var recipientKey = $"group:{groupName}";
            var groupMembers = group.ToList();

            var lines = options.GroupLineMode == GroupLineMode.OneLinePerParticipant
                ? BuildOneLinePerParticipant(eventData.EventTitle, groupMembers, options)
                : BuildSummaryLinesByFeeCategory(eventData.EventTitle, groupMembers, options);

            var draft = CreateDraft(
                draftKey: recipientKey,
                recipientKey: groupName,
                recipientName: groupName,
                recipientEmail: null,
                eventData: eventData,
                participants: groupMembers,
                lines: lines,
                options: options,
                fikenCustomers: fikenCustomers,
                savedGroupMappings: savedGroupMappings,
                existingDraftOrderReferences: existingDraftOrderReferences);

            drafts.Add(draft);
        }

        return drafts;
    }

    private static List<PlannedInvoiceDraft> BuildPerParticipantDrafts(
        MinSpeidingEventLoadResult eventData,
        List<EventParticipant> participants,
        InvoiceDraftPlanOptions options,
        IReadOnlyList<FikenContact> fikenCustomers,
        IReadOnlyDictionary<string, long> savedGroupMappings,
        IReadOnlySet<string>? existingDraftOrderReferences)
    {
        var drafts = new List<PlannedInvoiceDraft>();

        foreach (var participant in participants)
        {
            var recipientKey = $"participant:{participant.ParticipantKey}";
            var priceNok = ResolveEffectivePriceNok(participant, options.CategoryPricesNok, options.ParticipantOverrides);
            var priceOre = CurrencyHelper.ToOre(priceNok);
            var description = Truncate($"{eventData.EventTitle} – {participant.FeeLabel}", MaxFikenLineFieldLength);
            var effectiveGroupName = ResolveEffectiveGroupName(participant, options.ParticipantOverrides);
            var comment = string.Equals(effectiveGroupName, "Uten gruppe", StringComparison.OrdinalIgnoreCase)
                ? null
                : TruncateOrNull($"Gruppe: {effectiveGroupName}", MaxFikenLineFieldLength);

            var lines = new List<PlannedInvoiceLine>
            {
                new(
                    Description: description,
                    Quantity: 1m,
                    UnitPriceNok: priceNok,
                    UnitPriceOre: priceOre,
                    LineTotalOre: priceOre,
                    Comment: comment)
            };

            var displayName = string.IsNullOrWhiteSpace(participant.FullName)
                ? $"Deltaker {participant.ParticipantKey}"
                : participant.FullName;

            var draft = CreateDraft(
                draftKey: recipientKey,
                recipientKey: displayName,
                recipientName: displayName,
                recipientEmail: participant.Email,
                eventData: eventData,
                participants: [participant],
                lines: lines,
                options: options,
                fikenCustomers: fikenCustomers,
                savedGroupMappings: savedGroupMappings,
                existingDraftOrderReferences: existingDraftOrderReferences);

            drafts.Add(draft);
        }

        return drafts;
    }

    private static List<PlannedInvoiceLine> BuildOneLinePerParticipant(
        string eventTitle,
        List<EventParticipant> groupMembers,
        InvoiceDraftPlanOptions options)
    {
        var lines = new List<PlannedInvoiceLine>(groupMembers.Count);
        foreach (var member in groupMembers)
        {
            var priceNok = ResolveEffectivePriceNok(member, options.CategoryPricesNok, options.ParticipantOverrides);
            var priceOre = CurrencyHelper.ToOre(priceNok);
            var name = string.IsNullOrWhiteSpace(member.FullName) ? $"Medlem {member.MemberNo}" : member.FullName;
            var description = Truncate($"{name} ({member.FeeLabel})", MaxFikenLineFieldLength);
            var comment = TruncateOrNull(
                member.MemberNo.HasValue ? $"{eventTitle} – Medl.nr {member.MemberNo.Value}" : eventTitle,
                MaxFikenLineFieldLength);

            lines.Add(new PlannedInvoiceLine(
                Description: description,
                Quantity: 1m,
                UnitPriceNok: priceNok,
                UnitPriceOre: priceOre,
                LineTotalOre: priceOre,
                Comment: comment));
        }

        return lines;
    }

    private static List<PlannedInvoiceLine> BuildSummaryLinesByFeeCategory(
        string eventTitle,
        List<EventParticipant> groupMembers,
        InvoiceDraftPlanOptions options)
    {
        var pricedMembers = groupMembers
            .Select(m => new
            {
                Member = m,
                PriceNok = ResolveEffectivePriceNok(m, options.CategoryPricesNok, options.ParticipantOverrides)
            })
            .GroupBy(x => (x.Member.FeeCategoryKey, x.PriceNok))
            .OrderBy(g => g.First().Member.FeeId ?? long.MaxValue)
            .ThenBy(g => g.Key.PriceNok);

        var lines = new List<PlannedInvoiceLine>();
        foreach (var bucket in pricedMembers)
        {
            var count = bucket.Count();
            var first = bucket.First();
            var unitPriceOre = CurrencyHelper.ToOre(first.PriceNok);
            var totalOre = unitPriceOre * count;
            var names = string.Join(", ", bucket.Select(x => x.Member.FullName).Where(n => !string.IsNullOrWhiteSpace(n)));
            var description = Truncate($"{first.Member.FeeLabel} – {eventTitle}", MaxFikenLineFieldLength);
            var comment = TruncateOrNull(string.IsNullOrWhiteSpace(names) ? null : $"Deltakere: {names}", MaxFikenLineFieldLength);

            lines.Add(new PlannedInvoiceLine(
                Description: description,
                Quantity: count,
                UnitPriceNok: first.PriceNok,
                UnitPriceOre: unitPriceOre,
                LineTotalOre: totalOre,
                Comment: comment));
        }

        return lines;
    }

    private static PlannedInvoiceDraft CreateDraft(
        string draftKey,
        string recipientKey,
        string recipientName,
        string? recipientEmail,
        MinSpeidingEventLoadResult eventData,
        List<EventParticipant> participants,
        List<PlannedInvoiceLine> lines,
        InvoiceDraftPlanOptions options,
        IReadOnlyList<FikenContact> fikenCustomers,
        IReadOnlyDictionary<string, long> savedGroupMappings,
        IReadOnlySet<string>? existingDraftOrderReferences)
    {
        long? selectedContactId = null;
        long? suggestedContactId = null;
        CustomerMatchConfidence confidence;
        string matchReason;

        if (options.ManualRecipientContactIds.TryGetValue(draftKey, out var manualContactId)
            && fikenCustomers.Any(c => c.ContactId == manualContactId && c.Customer && !c.Inactive))
        {
            var manualCustomer = fikenCustomers.First(c => c.ContactId == manualContactId);
            selectedContactId = manualCustomer.ContactId;
            suggestedContactId = manualCustomer.ContactId;
            confidence = CustomerMatchConfidence.SavedMapping;
            matchReason = $"Valgt manuelt → {manualCustomer.Name}";
        }
        else
        {
            var match = CustomerMatcher.Match(recipientName, recipientEmail, fikenCustomers, savedGroupMappings);
            selectedContactId = match.MatchedContactId;
            suggestedContactId = match.SuggestedContactId;
            confidence = match.Confidence;
            matchReason = match.MatchReason;
        }

        var orderReference = BuildOrderReference(options.OrderReferencePrefix, eventData.ProjectId, string.Empty);
        var duplicateExists = !string.IsNullOrWhiteSpace(orderReference)
            && existingDraftOrderReferences is not null
            && existingDraftOrderReferences.Contains(orderReference);

        var totalOre = lines.Sum(l => l.LineTotalOre);
        var warnings = new List<string>();

        if (!selectedContactId.HasValue)
        {
            warnings.Add("Velg eller opprett Fiken-kunde for denne mottakeren.");
        }

        if (lines.Any(l => l.UnitPriceOre == 0))
        {
            warnings.Add("Inneholder minst én linje med pris 0 kr.");
        }

        if (duplicateExists)
        {
            warnings.Add($"Fakturautkast med ordrenummer «{orderReference}» finnes allerede i Fiken.");
        }

        if (recipientName.Equals("Uten gruppe", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Deltaker(e) mangler tilhørighet til speidergruppe i Min speiding.");
        }

        return new PlannedInvoiceDraft(
            DraftKey: draftKey,
            RecipientKey: recipientKey,
            RecipientName: recipientName,
            RecipientEmail: recipientEmail,
            OrderReference: orderReference,
            SelectedContactId: selectedContactId,
            SuggestedContactId: suggestedContactId,
            MatchConfidence: confidence,
            MatchReason: matchReason,
            Lines: lines,
            ParticipantKeys: participants.Select(p => p.ParticipantKey).ToList(),
            TotalOre: totalOre,
            DuplicateOrderReferenceExists: duplicateExists,
            Warnings: warnings);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return maxLength <= 1 ? trimmed[..maxLength] : trimmed[..(maxLength - 1)] + "…";
    }

    private static string? TruncateOrNull(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value, maxLength);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
