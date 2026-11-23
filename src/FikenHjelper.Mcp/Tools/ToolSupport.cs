using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FikenHjelper.Core.Fiken;
using ModelContextProtocol;

namespace FikenHjelper.Mcp.Tools;

internal static class ToolSupport
{
    /// <summary>Kompakt JSON uten escaping av æøå, så svarene bruker færre tokens.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(McpJsonUtilities.DefaultOptions)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(McpJsonUtilities.DefaultOptions.TypeInfoResolver, new DefaultJsonTypeInfoResolver()),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    /// <summary>Gjør forventede feil om til meldinger modellen kan forstå (isError). Aldri stacktrace eller token.</summary>
    public static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (FikenApiException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (TimeoutException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), NorwegianTimeZone).DateTime);

    public static DateOnly? ParseDate(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        throw new McpException($"{parameterName} må være på formatet ÅÅÅÅ-MM-DD.");
    }

    public static decimal? Kr(long? ore) => ore is { } value ? value / 100m : null;

    private static readonly TimeZoneInfo NorwegianTimeZone = FindNorwegianTimeZone();

    private static TimeZoneInfo FindNorwegianTimeZone()
    {
        foreach (var id in new[] { "Europe/Oslo", "W. Europe Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            {
                return tz;
            }
        }

        return TimeZoneInfo.Utc;
    }
}
