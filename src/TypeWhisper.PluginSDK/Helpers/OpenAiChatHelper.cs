using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
///     Static helper for OpenAI-compatible chat completion API calls. Shared by
///     LLM provider plugins so each plugin doesn't reimplement request shaping.
/// </summary>
public static class OpenAiChatHelper
{
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
    public static Task<string> SendChatCompletionAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        CancellationToken ct
    ) =>
        SendChatCompletionAsync(
            httpClient,
            baseUrl,
            apiKey,
            model,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: "max_tokens",
            reasoningEffort: null,
            temperature: 0.1
        );

    /// <inheritdoc cref="SendChatCompletionAsync(HttpClient, string, string, string, string, string, CancellationToken)" />
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
                maxOutputTokenParameter, reasoningEffort, temperature, stream: false));

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
    ///     Sends the same request body with <c>"stream": true</c> and yields each
    ///     <c>choices[0].delta.content</c> token as it arrives over the SSE
    ///     connection. Covers the whole OpenAI-compatible <c>/v1/chat/completions</c>
    ///     cohort (OpenAI, Groq, Cerebras, Fireworks, Gemini, Cohere, OpenRouter,
    ///     OpenAiCompatible).
    /// </summary>
    public static async IAsyncEnumerable<string> SendChatCompletionStreamingAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        [EnumeratorCancellation] CancellationToken ct,
        int? maxOutputTokens = 2048,
        string maxOutputTokenParameter = "max_tokens",
        string? reasoningEffort = null,
        double? temperature = 0.1
    )
    {
        var requestBody = JsonSerializer.Serialize(
            BuildRequestBody(model, systemPrompt, userText, maxOutputTokens,
                maxOutputTokenParameter, reasoningEffort, temperature, stream: true));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/chat/completions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        // ResponseHeadersRead so we start reading the body as it streams instead of
        // buffering the whole response (the batch path's SendWithErrorHandlingAsync
        // buffers, so we send + check the status line ourselves here).
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
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var payload = line[6..];
            if (payload == "[DONE]")
                yield break;

            if (ParseChatCompletionStreamDelta(payload) is { Length: > 0 } delta)
                yield return delta;
        }
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
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            }
        };

        if (temperature is not null)
            body["temperature"] = temperature.Value;
        if (maxOutputTokens is not null)
            body[maxOutputTokenParameter] = maxOutputTokens.Value;
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning_effort"] = reasoningEffort;
        if (stream)
            body["stream"] = true;

        return body;
    }

    /// <summary>
    ///     Extracts <c>choices[0].delta.content</c> from a single SSE
    ///     <c>chat.completion.chunk</c> <c>data:</c> payload, or <c>null</c> for a
    ///     contentless / unparseable frame (heartbeats, role-only first frames,
    ///     finish frames). Reflection-free (A18) via <see cref="JsonDocument" />.
    /// </summary>
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
    ///     Parses an OpenAI chat completion JSON response and returns the content of the first choice.
    /// </summary>
    internal static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (
                firstChoice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
            )
            {
                return content.GetString()?.Trim() ?? "";
            }
        }

        return "";
    }
}
