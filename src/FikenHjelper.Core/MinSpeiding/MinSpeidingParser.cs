using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FikenHjelper.Core.MinSpeiding;

public static partial class MinSpeidingParser
{
    private const string UnknownGroupName = "Uten gruppe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] PriceExtensionKeys =
    [
        "fee_amount",
        "total_amount",
        "fee",
        "total_fee",
        "price",
        "amount",
        "project_fee",
        "deltakeravgift"
    ];

    public static MinSpeidingEventLoadResult Parse(
        string projectId,
        string participantsJson,
        string? questionsJson = null,
        bool questionsEndpointAvailable = true,
        string? eventTitleOverride = null)
    {
        if (string.IsNullOrWhiteSpace(participantsJson))
        {
            throw new FormatException("Tomt svar fra Min speiding.");
        }

        var rawResponse = JsonSerializer.Deserialize<RawParticipantsResponse>(participantsJson, JsonOptions)
            ?? throw new FormatException("Kunne ikke lese deltakerdata fra Min speiding.");

        RawQuestionsResponse? rawQuestions = null;
        if (!string.IsNullOrWhiteSpace(questionsJson))
        {
            rawQuestions = JsonSerializer.Deserialize<RawQuestionsResponse>(questionsJson, JsonOptions);
        }

        return BuildResult(
            projectId,
            rawResponse,
            rawQuestions,
            questionsEndpointAvailable,
            eventTitleOverride,
            isDemoData: false);
    }

    public static MinSpeidingEventLoadResult BuildResult(
        string projectId,
        RawParticipantsResponse rawResponse,
        RawQuestionsResponse? rawQuestions,
        bool questionsEndpointAvailable,
        string? eventTitleOverride = null,
        bool isDemoData = false)
    {
        var feeLabels = rawResponse.Labels?.ProjectFee is { Count: > 0 }
            ? new Dictionary<string, string>(rawResponse.Labels.ProjectFee, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var questionsMap = rawQuestions?.Questions
            ?? new Dictionary<string, RawQuestion>(StringComparer.OrdinalIgnoreCase);

        var labelGroupLookup = ExtractGroupLabels(rawResponse.Labels);
        var eventTitle = ResolveEventTitle(eventTitleOverride, rawQuestions, projectId);
        var participants = new List<EventParticipant>();
        var extraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawResponse.Participants is not null)
        {
            foreach (var (participantKey, raw) in rawResponse.Participants)
            {
                var (groupName, groupId, troopName, availableGroups) = ResolveGroups(raw, labelGroupLookup);
                var (feeCategoryKey, feeLabel) = ResolveFeeCategory(raw.FeeId, feeLabels);
                var extraFields = ExtractExtraFields(raw.ExtensionData, extraKeys);
                var detectedPrice = DetectPriceNok(feeLabel, raw.ExtensionData);
                var answers = ResolveAnswers(raw.Questions, questionsMap);

                participants.Add(new EventParticipant(
                    ParticipantKey: participantKey,
                    MemberNo: raw.MemberNo,
                    FirstName: (raw.FirstName ?? string.Empty).Trim(),
                    LastName: (raw.LastName ?? string.Empty).Trim(),
                    Email: string.IsNullOrWhiteSpace(raw.PrimaryEmail) ? null : raw.PrimaryEmail.Trim(),
                    GroupName: groupName,
                    GroupId: groupId,
                    TroopName: troopName,
                    FeeId: raw.FeeId,
                    FeeLabel: feeLabel,
                    FeeCategoryKey: feeCategoryKey,
                    DetectedPriceNok: detectedPrice,
                    Cancelled: raw.Cancelled,
                    Confirmed: raw.Confirmed,
                    Attended: raw.Attended,
                    CheckedIn: raw.CheckedIn,
                    RegistrationDate: raw.RegistrationDate,
                    Answers: answers,
                    ExtraFields: extraFields,
                    AvailableGroups: availableGroups));
            }
        }

        var orderedParticipants = participants
            .OrderBy(p => p.GroupName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ThenBy(p => p.LastName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ThenBy(p => p.FirstName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ToList();

        var categories = SummarizeFeeCategories(orderedParticipants);

        return new MinSpeidingEventLoadResult(
            ProjectId: projectId,
            EventTitle: eventTitle,
            Participants: orderedParticipants,
            FeeCategories: categories,
            FeeLabels: feeLabels,
            ExtraParticipantPropertyNames: extraKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            QuestionsEndpointAvailable: questionsEndpointAvailable,
            IsDemoData: isDemoData);
    }

    /// <summary>Lager priskategori-sammendrag (antall og foreslått pris) fra deltakerne. Brukes også av filimporten.</summary>
    public static IReadOnlyList<FeeCategorySummary> SummarizeFeeCategories(IEnumerable<EventParticipant> participants)
        => participants
            .GroupBy(p => p.FeeCategoryKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var suggested = g.Where(p => !p.Cancelled).Select(p => p.DetectedPriceNok).FirstOrDefault(p => p > 0m)
                    ?? g.Select(p => p.DetectedPriceNok).FirstOrDefault(p => p > 0m)
                    ?? g.Select(p => p.DetectedPriceNok).FirstOrDefault(p => p.HasValue);
                return new FeeCategorySummary(
                    Key: g.Key,
                    FeeId: first.FeeId,
                    Label: first.FeeLabel,
                    ParticipantCount: g.Count(p => !p.Cancelled),
                    SuggestedPriceNok: suggested);
            })
            .OrderBy(c => c.FeeId ?? long.MaxValue)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static decimal? TryExtractPriceNokFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        if (label.Contains("gratis", StringComparison.OrdinalIgnoreCase)
            || label.Contains("free", StringComparison.OrdinalIgnoreCase))
        {
            return 0m;
        }

        var suffixMatch = SuffixCurrencyRegex().Match(label);
        if (suffixMatch.Success && TryParseNorwegianDecimal(suffixMatch.Groups[1].Value, out var suffixVal))
        {
            return suffixVal;
        }

        var prefixMatch = PrefixCurrencyRegex().Match(label);
        if (prefixMatch.Success && TryParseNorwegianDecimal(prefixMatch.Groups[1].Value, out var prefixVal))
        {
            return prefixVal;
        }

        return null;
    }

    private static string ResolveEventTitle(
        string? eventTitleOverride,
        RawQuestionsResponse? rawQuestions,
        string projectId)
    {
        if (!string.IsNullOrWhiteSpace(eventTitleOverride))
        {
            return eventTitleOverride.Trim();
        }

        var formTitle = rawQuestions?.Forms?.Values
            .Select(f => f.Title)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (!string.IsNullOrWhiteSpace(formTitle))
        {
            return formTitle.Trim();
        }

        return $"Arrangement {projectId}";
    }

    private static (string GroupName, long? GroupId, string? TroopName, IReadOnlyList<ParticipantGroupOption> AvailableGroups) ResolveGroups(
        RawParticipant raw,
        IReadOnlyDictionary<string, string>? labelGroupLookup)
    {
        var availableGroups = new List<ParticipantGroupOption>();

        void AddGroup(string? rawName, long? id, string? sourceLabel)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return;
            }

            var trimmed = rawName.Trim();
            if (IsIgnoredGroupName(trimmed))
            {
                return;
            }

            var existingIndex = availableGroups.FindIndex(g =>
                string.Equals(g.GroupName, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (!availableGroups[existingIndex].GroupId.HasValue && id.HasValue)
                {
                    availableGroups[existingIndex] = availableGroups[existingIndex] with { GroupId = id };
                }

                return;
            }

            availableGroups.Add(new ParticipantGroupOption(trimmed, id, sourceLabel));
        }

        AddGroup(
            raw.GroupRegistrationInfo?.GroupName,
            raw.GroupRegistrationInfo?.GroupId ?? raw.PrimaryMembershipInfo?.GroupId,
            "Påmeldt via gruppe");

        AddGroup(
            raw.PrimaryMembershipInfo?.GroupName,
            raw.PrimaryMembershipInfo?.GroupId,
            "Primærgruppe");

        // Groups found in other fields are offered as choices, but never become the invoice
        // recipient on their own: only the registration or primary membership can do that.
        var trustedGroupCount = availableGroups.Count;

        if (raw.ExtensionData is not null)
        {
            foreach (var (key, element) in raw.ExtensionData)
            {
                if (SkippedExtensionGroupKeys.Contains(key))
                {
                    continue;
                }

                ExtractGroupsFromElement(key, element, labelGroupLookup, AddGroup, depth: 0);
            }
        }

        var defaultGroupName = trustedGroupCount > 0 ? availableGroups[0].GroupName : UnknownGroupName;
        var defaultGroupId = trustedGroupCount > 0
            ? availableGroups[0].GroupId ?? raw.PrimaryMembershipInfo?.GroupId
            : null;

        return (defaultGroupName, defaultGroupId, TrimOrNull(raw.PrimaryMembershipInfo?.TroopName), availableGroups);
    }

    private static IReadOnlyDictionary<string, string>? ExtractGroupLabels(RawLabels? labels)
    {
        if (labels?.ExtensionData is null)
        {
            return null;
        }

        foreach (var labelKey in GroupLabelKeys)
        {
            var entry = labels.ExtensionData.FirstOrDefault(kv =>
                kv.Key.Equals(labelKey, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(entry.Key) && entry.Value.ValueKind == JsonValueKind.Object)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in entry.Value.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.Value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(val))
                        {
                            dict[prop.Name.Trim()] = val;
                        }
                    }
                }

                if (dict.Count > 0)
                {
                    return dict;
                }
            }
        }

        return null;
    }

    private static void ExtractGroupsFromElement(
        string key,
        JsonElement element,
        IReadOnlyDictionary<string, string>? labelGroupLookup,
        Action<string?, long?, string?> addGroup,
        int depth)
    {
        if (depth > 5)
        {
            return;
        }

        var keyMatchesGroup = IsGroupOrMembershipKey(key);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                ExtractGroupsFromObject(keyMatchesGroup, element, labelGroupLookup, addGroup, depth);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        ExtractGroupsFromElement(key, item, labelGroupLookup, addGroup, depth + 1);
                    }
                    else if (keyMatchesGroup)
                    {
                        TryAddGroupToken(item, explicitId: null, labelGroupLookup, addGroup, allowRawString: true);
                    }
                }
                break;

            case JsonValueKind.String when keyMatchesGroup && !PureIdGroupKeys.Contains(key):
                var text = element.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    break;
                }

                if (PluralGroupKeys.Contains(key) && (text.Contains(',') || text.Contains(';')))
                {
                    foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        TryAddGroupTokenText(part, explicitId: null, labelGroupLookup, addGroup, allowRawString: true);
                    }
                }
                else
                {
                    TryAddGroupTokenText(text, explicitId: null, labelGroupLookup, addGroup, allowRawString: true);
                }
                break;

            case JsonValueKind.Number when keyMatchesGroup && !PureIdGroupKeys.Contains(key):
                TryAddGroupToken(element, explicitId: null, labelGroupLookup, addGroup, allowRawString: false);
                break;
        }
    }

    private static void ExtractGroupsFromObject(
        bool keyMatchesGroup,
        JsonElement obj,
        IReadOnlyDictionary<string, string>? labelGroupLookup,
        Action<string?, long?, string?> addGroup,
        int depth)
    {
        string? directName = null;
        long? directId = null;

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                if (IsDirectGroupNameProperty(prop.Name, keyMatchesGroup) && directName is null)
                {
                    directName = prop.Value.GetString()?.Trim();
                }
            }

            if (directId is null && IsDirectGroupIdProperty(prop.Name))
            {
                directId = TryParseLongElement(prop.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(directName) && !IsPurelyNumericOrBool(directName))
        {
            addGroup(directName, directId, "Medlemskap");
            return;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                ExtractGroupsFromElement(prop.Name, prop.Value, labelGroupLookup, addGroup, depth + 1);
            }
            else if (keyMatchesGroup
                && prop.Value.ValueKind == JsonValueKind.String
                && long.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
            {
                // Only the id → name map form ({"501": "Molde speidergruppe"}). Other text properties
                // (status, dates, roles) are not group names.
                TryAddGroupTokenText(prop.Value.GetString(), mapId, labelGroupLookup, addGroup, allowRawString: true);
            }
        }
    }

    private static void TryAddGroupToken(
        JsonElement item,
        long? explicitId,
        IReadOnlyDictionary<string, string>? labelGroupLookup,
        Action<string?, long?, string?> addGroup,
        bool allowRawString)
    {
        var token = item.ValueKind switch
        {
            JsonValueKind.String => item.GetString()?.Trim(),
            JsonValueKind.Number => item.GetRawText(),
            _ => null
        };

        TryAddGroupTokenText(token, explicitId, labelGroupLookup, addGroup, allowRawString: allowRawString && item.ValueKind == JsonValueKind.String);
    }

    private static void TryAddGroupTokenText(
        string? token,
        long? explicitId,
        IReadOnlyDictionary<string, string>? labelGroupLookup,
        Action<string?, long?, string?> addGroup,
        bool allowRawString)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var trimmed = token.Trim();
        var idFromToken = long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId)
            ? parsedId
            : explicitId;

        if (labelGroupLookup is not null && labelGroupLookup.TryGetValue(trimmed, out var lookedUp))
        {
            addGroup(lookedUp, idFromToken, "Medlemskap");
            return;
        }

        if (allowRawString && !IsPurelyNumericOrBool(trimmed))
        {
            addGroup(trimmed, explicitId, "Medlemskap");
        }
    }

    // Only known group-list keys count. Matching any key that merely contains "group" or
    // "membership" turned fields like age_group or membership_status into scout groups.
    private static bool IsGroupOrMembershipKey(string key)
        => ExtensionGroupListKeys.Contains(key);

    private static bool IsDirectGroupNameProperty(string propName, bool keyMatchesGroup)
        => propName.Equals("group_name", StringComparison.OrdinalIgnoreCase)
            || propName.Equals("gruppe", StringComparison.OrdinalIgnoreCase)
            || propName.Equals("gruppenavn", StringComparison.OrdinalIgnoreCase)
            || (keyMatchesGroup && (propName.Equals("name", StringComparison.OrdinalIgnoreCase)
                || propName.Equals("title", StringComparison.OrdinalIgnoreCase)));

    private static bool IsDirectGroupIdProperty(string propName)
        => propName.Equals("group_id", StringComparison.OrdinalIgnoreCase)
            || propName.Equals("group_no", StringComparison.OrdinalIgnoreCase)
            || propName.Equals("id", StringComparison.OrdinalIgnoreCase);

    private static long? TryParseLongElement(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsPurelyNumericOrBool(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return true;
        }

        return decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsIgnoredGroupName(string value)
        => string.Equals(value, UnknownGroupName, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("deprecated", StringComparison.OrdinalIgnoreCase)
            || value.Contains("deprecated - use", StringComparison.OrdinalIgnoreCase)
            || value.Contains("use group_registration_info", StringComparison.OrdinalIgnoreCase)
            || value.Contains("use primary_membership_info", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] GroupLabelKeys = ["groups", "group"];

    private static readonly HashSet<string> SkippedExtensionGroupKeys = new(PriceExtensionKeys, StringComparer.OrdinalIgnoreCase)
    {
        "pc_details",
        "pc_courses",
        "ssno",
        "sex",
        "date_of_birth",
        "district_id",
        "district_name",
        "group_id",
        "group_name",
        "org_id",
        "org_name",
        "patrol_id",
        "patrol_name",
        "extra_purchases",
        "purchase_amount"
    };

    private static readonly HashSet<string> PureIdGroupKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "group_id",
        "group_no"
    };

    private static readonly HashSet<string> PluralGroupKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "groups",
        "group_names",
        "other_groups",
        "memberships",
        "all_groups",
        "secondary_groups"
    };

    private static readonly HashSet<string> ExtensionGroupListKeys = new(PluralGroupKeys, StringComparer.OrdinalIgnoreCase)
    {
        "other_memberships",
        "group_memberships",
        "additional_groups"
    };

    private static (string Key, string Label) ResolveFeeCategory(
        long? feeId,
        IReadOnlyDictionary<string, string> feeLabels)
    {
        if (feeId.HasValue)
        {
            var idString = feeId.Value.ToString(CultureInfo.InvariantCulture);
            var key = $"fee:{idString}";
            var label = feeLabels.TryGetValue(idString, out var found) && !string.IsNullOrWhiteSpace(found)
                ? found.Trim()
                : $"Avgift #{idString}";
            return (key, label);
        }

        return ("fee:none", "Standard / uten valgt avgift");
    }

    private static decimal? DetectPriceNok(
        string feeLabel,
        Dictionary<string, JsonElement>? extensionData)
    {
        var sawZero = false;
        if (extensionData is not null)
        {
            foreach (var priceKey in PriceExtensionKeys)
            {
                var entry = extensionData.FirstOrDefault(kv =>
                    kv.Key.Equals(priceKey, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(entry.Key)
                    && TryExtractDecimalElement(entry.Value, out var parsed)
                    && parsed >= 0m)
                {
                    if (parsed > 0m)
                    {
                        return parsed;
                    }

                    sawZero = true;
                }
            }
        }

        var fromLabel = TryExtractPriceNokFromLabel(feeLabel);
        if (fromLabel.HasValue)
        {
            return fromLabel.Value;
        }

        return sawZero ? 0m : null;
    }

    private static bool TryExtractDecimalElement(JsonElement element, out decimal value)
    {
        value = 0m;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && TryParseNorwegianDecimal(element.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> ExtractExtraFields(
        Dictionary<string, JsonElement>? extensionData,
        HashSet<string> discoveredKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (extensionData is null)
        {
            return result;
        }

        foreach (var (key, element) in extensionData)
        {
            discoveredKeys.Add(key);
            result[key] = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => element.GetRawText()
            };
        }

        return result;
    }

    private static Dictionary<string, string> ResolveAnswers(
        Dictionary<string, RawAnswerValue?>? rawAnswers,
        Dictionary<string, RawQuestion> questionsMap)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rawAnswers is null)
        {
            return resolved;
        }

        foreach (var (questionId, rawAnswer) in rawAnswers)
        {
            if (rawAnswer is null || string.IsNullOrWhiteSpace(rawAnswer.RawText))
            {
                continue;
            }

            if (questionsMap.TryGetValue(questionId, out var questionDef))
            {
                var label = string.IsNullOrWhiteSpace(questionDef.Question)
                    ? $"Spørsmål {questionId}"
                    : questionDef.Question.Trim();

                var displayValue = ResolveAnswerDisplay(rawAnswer, questionDef);
                resolved[label] = displayValue;
            }
            else
            {
                resolved[$"Spørsmål {questionId}"] = rawAnswer.RawText;
            }
        }

        return resolved;
    }

    private static string ResolveAnswerDisplay(RawAnswerValue rawAnswer, RawQuestion questionDef)
    {
        if (questionDef.Choices is not { Count: > 0 })
        {
            return rawAnswer.RawText;
        }

        var mapped = new List<string>(rawAnswer.Items.Count);
        foreach (var item in rawAnswer.Items)
        {
            if (questionDef.Choices.TryGetValue(item, out var choice) && !string.IsNullOrWhiteSpace(choice.Option))
            {
                mapped.Add(choice.Option.Trim());
                continue;
            }

            var matchingChoice = questionDef.Choices.Values.FirstOrDefault(c =>
                c.Value.HasValue && c.Value.Value.ToString(CultureInfo.InvariantCulture) == item);
            mapped.Add(!string.IsNullOrWhiteSpace(matchingChoice?.Option)
                ? matchingChoice.Option.Trim()
                : item);
        }

        return string.Join(", ", mapped);
    }

    private static bool TryParseNorwegianDecimal(string? input, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Trim().Replace(" ", string.Empty).Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"\b((?:\d{1,3}(?:\s\d{3})+|\d+)(?:[.,]\d{1,2})?)\s*(?:,-|kr\b|nok\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixCurrencyRegex();

    [GeneratedRegex(@"(?:kr\.?|nok)\s*((?:\d{1,3}(?:\s\d{3})+|\d+)(?:[.,]\d{1,2})?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixCurrencyRegex();
}
