using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.MinSpeiding;

public sealed class RawParticipantsResponse
{
    [JsonPropertyName("participants")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<RawParticipant>))]
    public Dictionary<string, RawParticipant>? Participants { get; set; }

    [JsonPropertyName("labels")]
    [JsonConverter(typeof(PhpObjectOrEmptyArrayConverter<RawLabels>))]
    public RawLabels? Labels { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class RawLabels
{
    [JsonPropertyName("member_status")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<string>))]
    public Dictionary<string, string>? MemberStatus { get; set; }

    [JsonPropertyName("project_fee")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<string>))]
    public Dictionary<string, string>? ProjectFee { get; set; }

    [JsonPropertyName("contact_type")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<string>))]
    public Dictionary<string, string>? ContactType { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class RawParticipant
{
    [JsonPropertyName("member_no")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? MemberNo { get; set; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("primary_email")]
    public string? PrimaryEmail { get; set; }

    [JsonPropertyName("registration_date")]
    public string? RegistrationDate { get; set; }

    [JsonPropertyName("cancelled_date")]
    public string? CancelledDate { get; set; }

    [JsonPropertyName("confirmed_at")]
    public string? ConfirmedAt { get; set; }

    [JsonPropertyName("cancelled")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Cancelled { get; set; }

    [JsonPropertyName("confirmed")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Confirmed { get; set; }

    [JsonPropertyName("attended")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool Attended { get; set; }

    [JsonPropertyName("checked_in")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool CheckedIn { get; set; }

    [JsonPropertyName("group_registration")]
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool GroupRegistration { get; set; }

    [JsonPropertyName("fee_id")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? FeeId { get; set; }

    [JsonPropertyName("member_status")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? MemberStatus { get; set; }

    [JsonPropertyName("primary_membership_info")]
    [JsonConverter(typeof(PhpObjectOrEmptyArrayConverter<RawMembershipInfo>))]
    public RawMembershipInfo? PrimaryMembershipInfo { get; set; }

    [JsonPropertyName("group_registration_info")]
    [JsonConverter(typeof(PhpObjectOrEmptyArrayConverter<RawMembershipInfo>))]
    public RawMembershipInfo? GroupRegistrationInfo { get; set; }

    [JsonPropertyName("questions")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<RawAnswerValue?>))]
    public Dictionary<string, RawAnswerValue?>? Questions { get; set; }

    [JsonPropertyName("contact_info")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<string>))]
    public Dictionary<string, string>? ContactInfo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class RawMembershipInfo
{
    [JsonPropertyName("group_id")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? GroupId { get; set; }

    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }

    [JsonPropertyName("troop_name")]
    public string? TroopName { get; set; }

    [JsonPropertyName("patrol_name")]
    public string? PatrolName { get; set; }

    [JsonPropertyName("district_name")]
    public string? DistrictName { get; set; }

    [JsonPropertyName("org_name")]
    public string? OrgName { get; set; }
}

public sealed class RawQuestionsResponse
{
    [JsonPropertyName("questions")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<RawQuestion>))]
    public Dictionary<string, RawQuestion>? Questions { get; set; }

    [JsonPropertyName("forms")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<RawForm>))]
    public Dictionary<string, RawForm>? Forms { get; set; }
}

public sealed class RawForm
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? Id { get; set; }

    [JsonPropertyName("form_type")]
    public string? FormType { get; set; }
}

public sealed class RawQuestion
{
    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("choices")]
    [JsonConverter(typeof(PhpDictionaryOrEmptyArrayConverter<RawQuestionChoice>))]
    public Dictionary<string, RawQuestionChoice>? Choices { get; set; }
}

public sealed class RawQuestionChoice
{
    [JsonPropertyName("value")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? Value { get; set; }

    [JsonPropertyName("option")]
    public string? Option { get; set; }
}

public sealed record ParticipantGroupOption(
    string GroupName,
    long? GroupId = null,
    string? SourceLabel = null);

public sealed record EventParticipant(
    string ParticipantKey,
    long? MemberNo,
    string FirstName,
    string LastName,
    string? Email,
    string GroupName,
    long? GroupId,
    string? TroopName,
    long? FeeId,
    string FeeLabel,
    string FeeCategoryKey,
    decimal? DetectedPriceNok,
    bool Cancelled,
    bool Confirmed,
    bool Attended,
    bool CheckedIn,
    string? RegistrationDate,
    IReadOnlyDictionary<string, string> Answers,
    IReadOnlyDictionary<string, string> ExtraFields,
    IReadOnlyList<ParticipantGroupOption> AvailableGroups)
{
    public string FullName => $"{FirstName} {LastName}".Trim();
    public bool HasMultipleGroups => AvailableGroups.Count > 1;
}

public sealed record FeeCategorySummary(
    string Key,
    long? FeeId,
    string Label,
    int ParticipantCount,
    decimal? SuggestedPriceNok);

public sealed record MinSpeidingEventLoadResult(
    string ProjectId,
    string EventTitle,
    IReadOnlyList<EventParticipant> Participants,
    IReadOnlyList<FeeCategorySummary> FeeCategories,
    IReadOnlyDictionary<string, string> FeeLabels,
    IReadOnlyList<string> ExtraParticipantPropertyNames,
    bool QuestionsEndpointAvailable,
    bool IsDemoData);
