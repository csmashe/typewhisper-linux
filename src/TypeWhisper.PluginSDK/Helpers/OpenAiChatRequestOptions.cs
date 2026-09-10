namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Request shaping for <see cref="OpenAiChatHelper" /> chat completions.</summary>
public sealed record OpenAiChatRequestOptions
{
    /// <summary>Minimum output token cap; raised by LlmOutputTokenBudget for long prompts. Null omits the field.</summary>
    public int? MaxOutputTokens { get; init; } = 2048;
    public string? ProviderName { get; init; }

    /// <summary>
    ///     When false, <see cref="MaxOutputTokens" /> is sent unchanged. Use for endpoints whose
    ///     context window is unknown or small (self-hosted servers), where a scaled cap can push an
    ///     otherwise valid request over the model's limit.
    /// </summary>
    public bool ScaleOutputTokens { get; init; } = true;
    public string MaxOutputTokenParameter { get; init; } = "max_tokens";
    public string? ReasoningEffort { get; init; }
    public double? Temperature { get; init; } = 0.1;

    /// <summary>
    ///     Provider-specific fields written after the standard ones. A key the helper already
    ///     writes (<c>model</c>, <c>messages</c>, <c>stream</c>, the output cap, ...) throws
    ///     <see cref="ArgumentException" />.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? AdditionalBodyFields { get; init; }
}
