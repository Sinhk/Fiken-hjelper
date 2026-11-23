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

        var eventTitle = ResolveEventTitle(eventTitleOverride, rawQuestions, projectId);
        var participants = new List<EventParticipant>();
        var extraKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawResponse.Participants is not null)
        {
            foreach (var (participantKey, raw) in rawResponse.Participants)
            {
                var (groupName, groupId, troopName) = ResolveGroup(raw);
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
                    ExtraFields: extraFields));
            }
        }

        var orderedParticipants = participants
            .OrderBy(p => p.GroupName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ThenBy(p => p.LastName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ThenBy(p => p.FirstName, StringComparer.Create(CultureInfo.GetCultureInfo("nb-NO"), ignoreCase: true))
            .ToList();

        var categories = orderedParticipants
            .GroupBy(p => p.FeeCategoryKey, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var suggested = g.Select(p => p.DetectedPriceNok).FirstOrDefault(p => p.HasValue);
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

    private static (string GroupName, long? GroupId, string? TroopName) ResolveGroup(RawParticipant raw)
    {
        if (!string.IsNullOrWhiteSpace(raw.GroupRegistrationInfo?.GroupName))
        {
            return (
                raw.GroupRegistrationInfo.GroupName.Trim(),
                raw.GroupRegistrationInfo.GroupId ?? raw.PrimaryMembershipInfo?.GroupId,
                TrimOrNull(raw.PrimaryMembershipInfo?.TroopName));
        }

        if (!string.IsNullOrWhiteSpace(raw.PrimaryMembershipInfo?.GroupName))
        {
            return (
                raw.PrimaryMembershipInfo.GroupName.Trim(),
                raw.PrimaryMembershipInfo.GroupId,
                TrimOrNull(raw.PrimaryMembershipInfo.TroopName));
        }

        return (UnknownGroupName, null, null);
    }

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
        if (extensionData is not null)
        {
            foreach (var priceKey in PriceExtensionKeys)
            {
                var entry = extensionData.FirstOrDefault(kv =>
                    kv.Key.Equals(priceKey, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDecimal(out var dec) && dec >= 0)
                    {
                        return dec;
                    }

                    if (entry.Value.ValueKind == JsonValueKind.String
                        && TryParseNorwegianDecimal(entry.Value.GetString(), out var strDec)
                        && strDec >= 0)
                    {
                        return strDec;
                    }
                }
            }
        }

        return TryExtractPriceNokFromLabel(feeLabel);
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

    [GeneratedRegex(@"(\d+(?:[.,]\d{1,2})?)\s*(?:,-|kr\b|nok\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixCurrencyRegex();

    [GeneratedRegex(@"(?:kr\.?|nok)\s*(\d+(?:[.,]\d{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixCurrencyRegex();
}
