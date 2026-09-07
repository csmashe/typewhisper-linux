using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Rejects provider responses that explicitly report an incomplete token-limited result.
/// </summary>
public static class LlmResponseTruncationGuard
{
    private static readonly string[] s_tokenLimitReasons =
    [
        "length",
        "max_tokens",
        "max_output_tokens",
        "model_context_window_exceeded",
    ];

    /// <summary>Rejects a token-limited OpenAI-compatible chat completion.</summary>
    public static void ThrowIfOpenAiChatCompletionTruncated(
        JsonElement root,
        string providerName)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return;
        }

        var firstChoice = choices[0];
        if (IsTokenLimitReason(GetString(firstChoice, "finish_reason"))
            || IsTokenLimitReason(GetString(firstChoice, "native_finish_reason")))
        {
            Throw(providerName);
        }
    }

    /// <summary>Rejects a token-limited Anthropic message response.</summary>
    public static void ThrowIfAnthropicResponseTruncated(
        JsonElement root,
        string providerName)
    {
        if (IsTokenLimitReason(GetString(root, "stop_reason")))
            Throw(providerName);
    }

    /// <summary>Rejects an incomplete Responses API result or completion event.</summary>
    public static void ThrowIfResponsesApiIncomplete(
        JsonElement root,
        string providerName)
    {
        if (TryCreateResponsesApiIncompleteException(root, providerName) is { } exception)
            throw exception;
    }

    /// <summary>
    ///     Non-throwing form of <see cref="ThrowIfResponsesApiIncomplete" /> for SSE policies,
    ///     which report failures as a decision rather than by throwing.
    /// </summary>
    public static PluginRequestException? TryCreateResponsesApiIncompleteException(
        JsonElement root,
        string providerName)
    {
        if (root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && TryCreateResponsesApiIncompleteException(response, providerName) is { } nested)
        {
            return nested;
        }

        var reason = root.TryGetProperty("incomplete_details", out var details)
                     && details.ValueKind == JsonValueKind.Object
            ? GetString(details, "reason")
            : null;
        if (IsTokenLimitReason(reason))
            return Truncated(providerName);

        var status = GetString(root, "status");
        return string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase)
            ? Incomplete(providerName)
            : null;
    }

    /// <summary>True for the finish/stop reasons providers use to report an output token limit.</summary>
    public static bool IsTokenLimitReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && s_tokenLimitReasons.Contains(reason, StringComparer.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void Throw(string providerName) =>
        throw Truncated(providerName);

    private static PluginRequestException Truncated(string providerName) =>
        new(
            $"{providerName} stopped the response at its output token limit.",
            PluginRequestFailureKind.OutputTruncated,
            isTransient: false);

    private static PluginRequestException Incomplete(string providerName) =>
        new(
            $"{providerName} returned an incomplete response.",
            PluginRequestFailureKind.OutputIncomplete,
            isTransient: false);
}
