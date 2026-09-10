using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiJson
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    public static JsonElement Element<T>(T value) =>
        JsonSerializer.SerializeToElement(value, s_jsonOptions).Clone();

    public static StringContent CreateJsonContent(IReadOnlyDictionary<string, JsonElement> body) =>
        new(JsonSerializer.Serialize(body, s_jsonOptions), Encoding.UTF8, "application/json");
}
