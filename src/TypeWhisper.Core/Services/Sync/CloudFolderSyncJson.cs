// Shared JSON contract for the Premium Cloud Folder Sync wire format, carried over from upstream
// alongside UserDataSync.cs. The serializer options and the Serialize/Deserialize pair are the
// public entry points for that feature, but its consumers were not ported to Linux, so the
// "unused" family below reports a feature that has not landed rather than a mistake. Keep public.
// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Provides cloud folder sync json behavior.
/// </summary>
public static class CloudFolderSyncJson
{
    /// <summary>
    /// Creates options.
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions(writeIndented: true);

    /// <summary>
    /// Serializes&lt;t&gt;.
    /// </summary>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes&lt;t&gt;.
    /// </summary>
    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);

    internal static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new CloudFolderSyncDateTimeConverter());
        return options;
    }
}

internal sealed class CloudFolderSyncDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>
    /// Reads.
    /// </summary>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // GetString throws InvalidOperationException on a number/bool/object token, which would
        // escape the deserializer instead of surfacing as the JsonException callers expect.
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
            throw new JsonException($"Expected an ISO-8601 date string, got {reader.TokenType}.");

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("Expected an ISO-8601 date string.");

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.UtcDateTime
            : throw new JsonException($"Invalid ISO-8601 date: {value}");
    }

    /// <summary>
    /// Writes.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        writer.WriteStringValue(utc.ToString(Format, CultureInfo.InvariantCulture));
    }
}
