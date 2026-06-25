// Non-"unused" inspections kept file-level (they cannot mask a future unused member): the
// 7-param overload is deliberate binary back-compat (test-pinned) so its redundant-looking
// defaults are required, and "Groq" is a provider name.
// ReSharper disable RedundantOverload.Global
// ReSharper disable MethodOverloadWithOptionalParameter
// ReSharper disable CommentTypo
// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
///     Static helper for OpenAI-compatible chat completion API calls. Shared by
///     LLM provider plugins so each plugin doesn't reimplement request shaping.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class OpenAiChatHelper
{
    /// <summary>
    ///     Convenience overload that sends a chat completion using the default token cap
    ///     (2048 via <c>max_tokens</c>), no reasoning-effort hint, and temperature 0.1.
    /// </summary>
    /// <returns>The assistant's response content text.</returns>
    /// <remarks>
    ///     Kept as a distinct signature for binary back-compat with plugins compiled
    ///     against it; pinned by the <c>PreservesLegacySevenParameterOverload</c> test.
    ///     (Rider flags it "redundant overload" — a false positive given that guarantee.)
    /// </remarks>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static Task<string> SendChatCompletionAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct
    )
    {
        return SendChatCompletionAsync(
            httpClient,
            baseUrl,
            apiKey,
            model,
            systemPrompt,
            userText,
            ct,
            2048,
            "max_tokens",
            null,
            0.1
        );
    }

    /// <summary>
    ///     Sends a chat completion request to an OpenAI-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "gpt-4o").</param>
    /// <param name="systemPrompt">System prompt text.</param>
    /// <param name="userText">User message text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxOutputTokens">
    ///     Optional cap on response tokens. Pass <c>null</c> to omit the field
    ///     entirely (some endpoints reject zero/empty values).
    /// </param>
    /// <param name="maxOutputTokenParameter">
    ///     Body field name for the token cap. Defaults to <c>"max_tokens"</c>;
    ///     newer GPT-5 / o-series chat-completion endpoints use
    ///     <c>"max_completion_tokens"</c>.
    /// </param>
    /// <param name="reasoningEffort">
    ///     Optional reasoning effort hint (low/medium/high). Only emitted when
    ///     non-empty.
    /// </param>
    /// <param name="temperature">
    ///     Optional sampling temperature. Pass <c>null</c> to omit the field —
    ///     required for models (e.g. GPT-5 with reasoning_effort set) that
    ///     reject the parameter outright.
    /// </param>
    /// <returns>The assistant's response content text.</returns>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static async Task<string> SendChatCompletionAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct,
        int? maxOutputTokens = 2048,
        string maxOutputTokenParameter = "max_tokens",
        string? reasoningEffort = null,
        double? temperature = 0.1
    )
    {
        var requestBody = JsonSerializer.Serialize(
            BuildRequestBody(model, systemPrompt, userText, maxOutputTokens,
                maxOutputTokenParameter, reasoningEffort, temperature, false));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/chat/completions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    /// <summary>
    ///     Streaming sibling of <see cref="SendChatCompletionAsync(HttpClient, string, string, string, string, string, CancellationToken, int?, string, string?, double?)" />.
    ///     Sends the same body with <c>"stream": true</c> and yields each <c>choices[0].delta.content</c> token
    ///     over SSE. Covers the full OpenAI-compatible cohort (OpenAI, Groq, Cerebras, Fireworks, Gemini, Cohere, OpenRouter).
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    public static async IAsyncEnumerable<string> SendChatCompletionStreamingAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        [EnumeratorCancellation]
        CancellationToken ct,
        int? maxOutputTokens = 2048,
        string maxOutputTokenParameter = "max_tokens",
        string? reasoningEffort = null,
        double? temperature = 0.1
    )
    {
        var requestBody = JsonSerializer.Serialize(
            BuildRequestBody(model, systemPrompt, userText, maxOutputTokens,
                maxOutputTokenParameter, reasoningEffort, temperature, true));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/chat/completions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // ResponseHeadersRead: start reading the body as it streams rather than buffering.
        // The batch path uses SendWithErrorHandlingAsync (which buffers), so here we send and
        // check the status line ourselves.
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var message = (int)response.StatusCode switch
            {
                401 => "Invalid API key",
                429 => "Rate limit reached, please wait",
                _ => $"API error {(int)response.StatusCode}: {OpenAiApiHelper.ExtractErrorMessage(errorBody)}"
            };
            throw new InvalidOperationException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } rawLine)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            // SSE spec makes the space after "data:" optional; strip at most one
            // so "data:{...}" frames aren't silently skipped.
            var payload = line[5..];
            if (payload.StartsWith(' '))
            {
                payload = payload[1..];
            }

            if (payload == "[DONE]")
            {
                yield break;
            }

            // Providers can fail mid-stream via a top-level `error` frame after a 200.
            // Throw so LlmStreamPump faults and the caller falls back to batch,
            // rather than committing partial deltas as a successful result.
            if (ParseChatCompletionStreamError(payload) is { } error)
            {
                throw new InvalidOperationException(error);
            }

            if (ParseChatCompletionStreamDelta(payload) is { Length: > 0 } delta)
            {
                yield return delta;
            }
        }
    }

    /// <summary>
    ///     Extracts <c>choices[0].delta.content</c> from a single SSE chunk payload,
    ///     or <c>null</c> for contentless/unparseable frames (heartbeats, role-only, finish).
    ///     Reflection-free via <see cref="JsonDocument" />.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    internal static string? ParseChatCompletionStreamDelta(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns an error message when an SSE <c>data:</c> payload is a top-level
    ///     <c>error</c> frame (OpenAI-compatible providers emit these mid-stream after a 200),
    ///     otherwise <c>null</c>. A literal <c>"error": null</c> is not treated as failure.
    ///     Reflection-free via <see cref="JsonDocument" />.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    internal static string? ParseChatCompletionStreamError(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out var error)
                || error.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "Streaming error.";
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "Streaming error.";
            }

            return "Streaming error.";
        }
    }

    /// <summary>Returns <c>choices[0].message.content</c> from a chat completion JSON response.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    private static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return "";
        }

        var firstChoice = choices[0];
        if (
            !firstChoice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
        )
        {
            return "";
        }

        return content.GetString()?.Trim() ?? "";
    }

    private static Dictionary<string, object?> BuildRequestBody(
        string model,
        string systemPrompt,
        string userText,
        int? maxOutputTokens,
        string maxOutputTokenParameter,
        string? reasoningEffort,
        double? temperature,
        bool stream
    )
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt }, new { role = "user", content = userText }
            }
        };

        if (temperature is not null)
        {
            body["temperature"] = temperature.Value;
        }

        if (maxOutputTokens is not null)
        {
            body[maxOutputTokenParameter] = maxOutputTokens.Value;
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        if (stream)
        {
            body["stream"] = true;
        }

        return body;
    }
}
