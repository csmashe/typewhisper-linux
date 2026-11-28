using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>
///     Rewrites Mistral error bodies into the <c>{"error":{"message":…}}</c> shape the SDK error handling
///     reads, so chat failures show Mistral's sentence instead of raw JSON. Only the message is kept:
///     validation errors also echo request fields.
/// </summary>
internal sealed class MistralErrorHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (Message(body) is not { } message)
                return response;

            var original = response.Content;
            response.Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json");
            original.Dispose();
            return response;
        }
        catch
        {
            // Nobody owns the response until it is returned.
            response.Dispose();
            throw;
        }
    }

    /// <summary>The transcription and model-list error text: Mistral's message, else the body.</summary>
    internal static string Describe(HttpResponseMessage response, string body) =>
        $"Mistral API error {(int)response.StatusCode}: {Message(body) ?? body}";

    /// <summary>
    ///     Reads <c>message</c> or <c>error.message</c> as text, or the <c>msg</c> entries of a validation
    ///     error's <c>detail</c> list, at the top level or inside <c>message</c>. The SDK caps the length
    ///     after redacting credentials, so a cut here could leave part of a key behind.
    /// </summary>
    internal static string? Message(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var text = root.TryGetProperty("message", out var message) ? Text(message)
                : root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var errorMessage) ? Text(errorMessage)
                : Text(root);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Object when element.TryGetProperty("detail", out var detail) => Detail(detail),
        _ => null,
    };

    private static string? Detail(JsonElement detail) => detail.ValueKind switch
    {
        JsonValueKind.String => detail.GetString(),
        JsonValueKind.Array => string.Join("; ", detail.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object
                            && item.TryGetProperty("msg", out var msg)
                            && msg.ValueKind == JsonValueKind.String
                ? msg.GetString()?.Replace("Assertion failed, ", "", StringComparison.Ordinal)
                : null)
            .Where(text => !string.IsNullOrWhiteSpace(text))),
        _ => null,
    };
}
