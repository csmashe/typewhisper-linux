using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Reads enum values like <see cref="JsonStringEnumConverter{TEnum}" /> but maps any
///     unrecognized name or out-of-range number to a configured fallback instead of throwing,
///     so one unknown value published in a future registry degrades a single field rather than
///     failing the whole deserialization. Writing delegates to the strict converter unchanged.
/// </summary>
internal sealed class TolerantJsonStringEnumConverter<TEnum>(TEnum fallback) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly JsonConverter<TEnum> s_strict = (JsonConverter<TEnum>)
        new JsonStringEnumConverter<TEnum>()
            .CreateConverter(typeof(TEnum), JsonSerializerOptions.Default);

    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse(reader.GetString(), ignoreCase: true, out TEnum named)
                    && Enum.IsDefined(named)
                    ? named
                    : fallback;
            case JsonTokenType.Number:
                if (!reader.TryGetInt32(out var numeric))
                {
                    return fallback;
                }

                var value = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
                return Enum.IsDefined(value) ? value : fallback;
            default:
                // Not an enum-shaped token at all (object, array, bool, ...): structural
                // corruption, which must still surface as the strict converter's JsonException.
                return s_strict.Read(ref reader, typeToConvert, options);
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        s_strict.Write(writer, value, options);
    }
}
