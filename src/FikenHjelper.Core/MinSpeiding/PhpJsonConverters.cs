using System.Text.Json;
using System.Text.Json.Serialization;

namespace FikenHjelper.Core.MinSpeiding;

/// <summary>
/// PHP endpoints sometimes serialize an empty object as an empty JSON array `[]`.
/// This converter parses `{...}` into <typeparamref name="T"/> and treats `[]` or `null` as `null`.
/// </summary>
public sealed class PhpObjectOrEmptyArrayConverter<T> : JsonConverter<T?> where T : class
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            SkipArray(ref reader);
            return null;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            return JsonSerializer.Deserialize<T>(ref reader, options);
        }

        throw new JsonException($"Expected JSON object, empty array, or null for {typeof(T).Name}, got {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value, options);
    }

    internal static void SkipArray(ref Utf8JsonReader reader)
    {
        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == depth)
            {
                return;
            }
        }
    }
}

/// <summary>
/// PHP endpoints sometimes serialize an empty associative map as `[]` instead of `{}`.
/// </summary>
public sealed class PhpDictionaryOrEmptyArrayConverter<TValue> : JsonConverter<Dictionary<string, TValue>?>
{
    public override Dictionary<string, TValue>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            PhpObjectOrEmptyArrayConverter<object>.SkipArray(ref reader);
            return new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected JSON object or empty array for dictionary, got {reader.TokenType}.");
        }

        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected PropertyName while reading dictionary, got {reader.TokenType}.");
            }

            var key = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading dictionary value.");
            }

            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            if (value is not null)
            {
                result[key] = value;
            }
        }

        throw new JsonException("Unexpected end of JSON while reading dictionary.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, TValue>? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value, options);
    }
}

[JsonConverter(typeof(ScoutnetAnswerValueConverter))]
public sealed record RawAnswerValue(string RawText, IReadOnlyList<string> Items);

public sealed class ScoutnetAnswerValueConverter : JsonConverter<RawAnswerValue?>
{
    public override RawAnswerValue? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
            {
                var str = reader.GetString() ?? string.Empty;
                return new RawAnswerValue(str, string.IsNullOrEmpty(str) ? [] : [str]);
            }

            case JsonTokenType.Number:
            {
                var numStr = reader.TryGetInt64(out var l)
                    ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new RawAnswerValue(numStr, [numStr]);
            }

            case JsonTokenType.True:
                return new RawAnswerValue("true", ["true"]);

            case JsonTokenType.False:
                return new RawAnswerValue("false", ["false"]);

            case JsonTokenType.StartArray:
            {
                var items = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    var item = reader.TokenType switch
                    {
                        JsonTokenType.String => reader.GetString(),
                        JsonTokenType.Number => reader.TryGetInt64(out var l)
                            ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JsonTokenType.True => "true",
                        JsonTokenType.False => "false",
                        JsonTokenType.Null => null,
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        items.Add(item.Trim());
                    }
                }

                return new RawAnswerValue(string.Join(", ", items), items);
            }

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, RawAnswerValue? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Items.Count > 1)
        {
            JsonSerializer.Serialize(writer, value.Items, options);
            return;
        }

        writer.WriteStringValue(value.RawText);
    }
}

public sealed class FlexibleNullableLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var val))
                {
                    return val;
                }
                if (reader.TryGetDouble(out var dbl))
                {
                    return (long)dbl;
                }
                return null;
            case JsonTokenType.String:
                var str = reader.GetString();
                return long.TryParse(str, out var parsed) ? parsed : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
            case JsonTokenType.Null:
                return false;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var n) && n != 0;
            case JsonTokenType.String:
            {
                var str = (reader.GetString() ?? string.Empty).Trim();
                return str.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || str.Equals("1", StringComparison.Ordinal)
                    || str.Equals("yes", StringComparison.OrdinalIgnoreCase)
                    || str.Equals("ja", StringComparison.OrdinalIgnoreCase);
            }
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
