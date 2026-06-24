using System.Text.Json;

namespace TypeWhisper.Cli.Output;

/// <summary>
///     JSON helpers for rendering API responses: scalar property extraction for
///     the human-readable tables, pretty-printing for <c>--json</c> output, and
///     error-message extraction from the API's error envelope.
/// </summary>
internal static class JsonFormatting
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static string Prop(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    public static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, s_jsonOptions);
        }
        catch
        {
            return json;
        }
    }

    public static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (
                    error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                )
                {
                    return message.GetString() ?? body;
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? body;
                }
            }
        }
        catch
        {
            // ignored
        }

        return body;
    }
}