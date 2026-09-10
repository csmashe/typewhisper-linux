using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Cli.Output;

/// <summary>
///     JSON helpers for rendering API responses: pretty-printing for <c>--json</c>
///     output, and error-message extraction from the API's error envelope.
/// </summary>
internal static class JsonFormatting
{
    private static readonly JsonWriterOptions s_writerOptions = new() { Indented = true };

    /// <summary>Re-serializes <paramref name="json" /> indented, returning the input unchanged if it isn't valid JSON.</summary>
    public static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // WriteTo rather than JsonSerializer.Serialize: writing an already-parsed
            // document needs no reflection, so this survives trimming (IL2026).
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, s_writerOptions))
            {
                doc.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch
        {
            return json;
        }
    }

    /// <summary>
    ///     Pulls the human-readable message out of the API's error envelope (either
    ///     <c>{"error":{"message":…}}</c> or <c>{"error":"…"}</c>), falling back to
    ///     the raw <paramref name="body" /> when no recognizable envelope is present.
    /// </summary>
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