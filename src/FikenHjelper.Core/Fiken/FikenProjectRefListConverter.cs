using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.Fiken;

/// <summary>
/// Fiken returnerer <c>project</c> som et enkelt objekt på fakturaer/salg, men som en liste på kjøp
/// (og posteringer kan ha flere prosjekter). Denne konverteren godtar objekt, liste eller null og
/// gir alltid en liste.
/// </summary>
public sealed class FikenProjectRefListConverter : JsonConverter<IReadOnlyList<FikenProjectRef>?>
{
    public override bool HandleNull => true;

    public override IReadOnlyList<FikenProjectRef>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return [];
            case JsonTokenType.StartObject:
                var single = JsonSerializer.Deserialize<FikenProjectRef>(ref reader, options);
                return single is null ? [] : [single];
            case JsonTokenType.StartArray:
                var list = JsonSerializer.Deserialize<List<FikenProjectRef?>>(ref reader, options);
                return list?.OfType<FikenProjectRef>().ToList() ?? [];
            default:
                reader.Skip();
                return [];
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<FikenProjectRef>? value,
        JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value?.ToList(), options);
    }
}
