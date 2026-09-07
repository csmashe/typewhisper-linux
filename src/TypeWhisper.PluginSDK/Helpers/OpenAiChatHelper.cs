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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
///     Static helper for OpenAI-compatible chat completion API calls. Shared by
///     LLM provider plugins so each plugin doesn't reimplement request shaping.
/// </summary>
// ReSharper disable once UnusedType.Global
public static class OpenAiChatHelper
{
    private const string ReasoningOnlyResponseMessage =
        "Chat completion returned only reasoning content and no final answer.";

    private static readonly JsonSerializerOptions s_requestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    ///     Convenience overload that sends a chat completion using the default minimum token cap
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
            new OpenAiChatRequestOptions(),
            ct
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
    ///     Optional minimum cap on response tokens. Pass <c>null</c> to omit the field
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
        var options = new OpenAiChatRequestOptions
        {
            MaxOutputTokens = maxOutputTokens,
            MaxOutputTokenParameter = maxOutputTokenParameter,
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
        };
        return await SendChatCompletionAsync(
            httpClient, baseUrl, apiKey, model, systemPrompt, userText, options, ct);
    }

    /// <summary>Sends a chat completion shaped by <paramref name="options" />.</summary>
    /// <returns>The assistant's response content text, with reasoning blocks removed.</returns>
    public static async Task<string> SendChatCompletionAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        OpenAiChatRequestOptions options,
        CancellationToken ct
    )
    {
        var requestBody = JsonSerializer.Serialize(
            BuildRequestBody(model, systemPrompt, userText, options, false), s_requestJsonOptions);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/chat/completions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json, options.ProviderName ?? "The provider");
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
        var options = new OpenAiChatRequestOptions
        {
            MaxOutputTokens = maxOutputTokens,
            MaxOutputTokenParameter = maxOutputTokenParameter,
            ReasoningEffort = reasoningEffort,
            Temperature = temperature,
        };
        await foreach (var delta in SendChatCompletionStreamingAsync(
            httpClient, baseUrl, apiKey, model, systemPrompt, userText, options, ct))
            yield return delta;
    }

    /// <summary>Streaming sibling of the options-based overload; reasoning blocks are filtered out of the deltas.</summary>
    public static async IAsyncEnumerable<string> SendChatCompletionStreamingAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        string systemPrompt,
        string userText,
        OpenAiChatRequestOptions options,
        [EnumeratorCancellation]
        CancellationToken ct
    )
    {
        var requestBody = JsonSerializer.Serialize(
            BuildRequestBody(model, systemPrompt, userText, options, true), s_requestJsonOptions);

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
        using var response = await SendStreamingRequestAsync(httpClient, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var message = (int)response.StatusCode switch
            {
                401 => "Invalid API key",
                429 => "Rate limit reached, please wait",
                _ => $"API error {(int)response.StatusCode}: {OpenAiApiHelper.ExtractErrorMessage(errorBody)}",
            };
            throw new InvalidOperationException(message);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var filter = new ThinkingBlockStreamFilter();
        var producedVisibleText = false;
        await foreach (var delta in SseEventDecoder.ReadValidatedAsync(reader, new ChatCompletionSsePolicy(options.ProviderName ?? "The provider"), ct))
        {
            foreach (var visible in filter.Push(delta))
            {
                producedVisibleText = true;
                yield return visible;
            }
        }

        var remaining = filter.Flush();
        if (remaining.Length > 0)
        {
            producedVisibleText = true;
            yield return remaining;
        }

        if (!producedVisibleText && filter.SawThinkBlock)
            throw new InvalidOperationException(ReasoningOnlyResponseMessage);
    }

    private static async Task<HttpResponseMessage> SendStreamingRequestAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken ct
    )
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("API request timed out.", ex);
        }
    }

    /// <summary>
    ///     Extracts <c>choices[0].delta.content</c> from a single SSE chunk payload,
    ///     or <c>null</c> for valid contentless frames (role-only, finish).
    ///     Reflection-free via <see cref="JsonDocument" />.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    internal static string? ParseChatCompletionStreamDelta(string dataPayload)
    {
        return ParseChatCompletionStreamDelta(dataPayload, out _);
    }

    /// <summary>
    ///     Extracts a content delta and reports whether <c>choices[0].finish_reason</c>
    ///     carries a valid terminal value. Valid contentless frames return <c>null</c>.
    /// </summary>
    private static string? ParseChatCompletionStreamDelta(
        string dataPayload,
        out string? terminalReason
    )
    {
        terminalReason = null;
        using var doc = JsonDocument.Parse(dataPayload);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw CreateInvalidResponseException(
                dataPayload,
                root,
                "'choices' must be a non-empty array"
            );
        }

        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object
            || !firstChoice.TryGetProperty("delta", out var delta)
            || delta.ValueKind != JsonValueKind.Object)
        {
            throw CreateInvalidResponseException(
                dataPayload,
                root,
                "'choices[0].delta' must be an object"
            );
        }

        // finish_reason is only ever null (still streaming) or a non-empty string
        // ("stop", "length", ...); anything else must not mask a truncated stream.
        terminalReason = firstChoice.TryGetProperty("finish_reason", out var finishReason)
                          && finishReason.ValueKind == JsonValueKind.String
                          ? finishReason.GetString() : null;

        if (!delta.TryGetProperty("content", out var content)
            || content.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (content.ValueKind != JsonValueKind.String)
        {
            throw CreateInvalidResponseException(
                dataPayload,
                root,
                "'choices[0].delta.content' must be a string"
            );
        }

        return content.GetString();
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

    private sealed class ChatCompletionSsePolicy(string providerName) : ISseEventPolicy<string>
    {
        public string StreamName => "chat completion stream";
        public string ExpectedTerminal => "[DONE] or a non-empty finish_reason";

        public SsePolicyDecision<string> Evaluate(SseEvent sseEvent)
        {
            if (sseEvent.Data == "[DONE]")
            {
                return new SsePolicyDecision<string>(
                    AcceptTerminal: true,
                    EndStream: true);
            }

            if (ParseChatCompletionStreamError(sseEvent.Data) is { } error)
            {
                return new SsePolicyDecision<string>(
                    Error: new InvalidOperationException(error));
            }

            var delta = ParseChatCompletionStreamDelta(
                sseEvent.Data,
                out var finishReason);
            if (LlmResponseTruncationGuard.IsTokenLimitReason(finishReason))
                return new SsePolicyDecision<string>(Error: new PluginRequestException(
                    $"{providerName} stopped the response at its output token limit.",
                    PluginRequestFailureKind.OutputTruncated, isTransient: false));
            return new SsePolicyDecision<string>(
                HasDelta: delta is { Length: > 0 },
                Delta: delta,
                AcceptTerminal: !string.IsNullOrEmpty(finishReason));
        }
    }

    /// <summary>Returns <c>choices[0].message.content</c> from a chat completion JSON response.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    private static string ParseChatCompletionResponse(string json, string providerName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw CreateInvalidResponseException(
                json,
                root,
                "'choices' must be a non-empty array"
            );
        }

        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, providerName);

        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object
            || !firstChoice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
        {
            throw CreateInvalidResponseException(
                json,
                root,
                "'choices[0].message' must be an object"
            );
        }

        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw CreateInvalidResponseException(
                json,
                root,
                "'choices[0].message.content' must be a string"
            );
        }

        var rawContent = content.GetString() ?? "";
        var stripped = ThinkingBlockFilter.Strip(rawContent);
        if (rawContent.Length > 0 && string.IsNullOrWhiteSpace(stripped))
            throw new InvalidOperationException(ReasoningOnlyResponseMessage);
        return stripped.Trim();
    }

    private static InvalidOperationException CreateInvalidResponseException(
        string json,
        JsonElement root,
        string requiredField
    )
    {
        var providerError = TryGetProviderErrorMessage(root);
        var providerErrorDetail = providerError is null
            ? ""
            : $" Provider error: {providerError}";
        return new InvalidOperationException(
            $"Invalid chat completion response: required field {requiredField}."
            + $"{providerErrorDetail} Body: {GetBodySnippet(json)}"
        );
    }

    private static string? TryGetProviderErrorMessage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return null;
    }

    private static string GetBodySnippet(string json)
    {
        const int maxLength = 200;
        return json.Length > maxLength ? $"{json[..maxLength]}..." : json;
    }

    private static Dictionary<string, object?> BuildRequestBody(
        string model,
        string systemPrompt,
        string userText,
        OpenAiChatRequestOptions options,
        bool stream
    )
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt }, new { role = "user", content = userText },
            },
        };

        if (options.Temperature is not null)
        {
            body["temperature"] = options.Temperature.Value;
        }

        if (options.MaxOutputTokens is not null)
        {
            // Preserve the configured floor while allowing long prompts enough output.
            // Reasoning consumes the same output cap, so reserve extra capacity for it.
            var budget = !options.ScaleOutputTokens
                ? options.MaxOutputTokens.Value
                : !string.IsNullOrWhiteSpace(options.ReasoningEffort)
                    ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                    : LlmOutputTokenBudget.Calculate(systemPrompt, userText);
            body[options.MaxOutputTokenParameter] = Math.Max(options.MaxOutputTokens.Value, budget);
        }

        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            body["reasoning_effort"] = options.ReasoningEffort;
        }

        if (stream)
        {
            body["stream"] = true;
        }

        if (options.AdditionalBodyFields is not { } fields)
            return body;

        foreach (var (key, value) in fields)
        {
            // Protect routing and stream semantics from provider-specific additions.
            if (key is "model" or "messages" or "stream")
                throw new ArgumentException($"Additional body field '{key}' is reserved.", nameof(options));
            body[key] = value;
        }

        return body;
    }
}
