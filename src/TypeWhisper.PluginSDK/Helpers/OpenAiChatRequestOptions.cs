namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>Request shaping for <see cref="OpenAiChatHelper" /> chat completions.</summary>
public sealed record OpenAiChatRequestOptions
{
    public int? MaxOutputTokens { get; init; } = 2048;
    public string MaxOutputTokenParameter { get; init; } = "max_tokens";
    public string? ReasoningEffort { get; init; }
    public double? Temperature { get; init; } = 0.1;

    /// <summary>
    ///     Provider-specific fields written after the standard ones. <c>model</c>, <c>messages</c>
    ///     and <c>stream</c> are reserved and throw <see cref="ArgumentException" />.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? AdditionalBodyFields { get; init; }
}
