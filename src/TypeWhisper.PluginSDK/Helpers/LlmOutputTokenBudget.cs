namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Calculates a bounded prompt action response budget from the supplied prompt text.
/// </summary>
public static class LlmOutputTokenBudget
{
    /// <summary>The legacy minimum response budget.</summary>
    // ReSharper disable once MemberCanBePrivate.Global -- public plugin-SDK surface
    public const int MinimumTokens = 2048;
    /// <summary>
    ///     The maximum response budget requested from providers. Kept at the output ceiling of
    ///     older models (GPT-4o 2024-05 snapshots, Claude 3 Haiku); a cap above a model's limit
    ///     is rejected outright rather than clamped.
    /// </summary>
    // ReSharper disable once MemberCanBePrivate.Global -- public plugin-SDK surface
    public const int MaximumTokens = 4096;
    /// <summary>Additional output capacity reserved for reasoning tokens.</summary>
    public const int ReasoningReserveTokens = 25_000;

    private const int CharactersPerEstimatedToken = 4;
    private const int LongInputThresholdTokens = 1024;
    private const int CompletionReserveTokens = 2048;
    private const int BudgetStepTokens = 256;

    /// <summary>
    /// Returns an output budget that preserves the legacy cap for short input and scales for long prompt actions.
    /// </summary>
    public static int Calculate(string? systemPrompt, string? userText)
    {
        var totalCharacters = (long)(systemPrompt?.Length ?? 0) + (userText?.Length ?? 0);
        var estimatedInputTokens = (totalCharacters + CharactersPerEstimatedToken - 1)
            / CharactersPerEstimatedToken;
        if (estimatedInputTokens <= LongInputThresholdTokens)
            return MinimumTokens;

        var desired = estimatedInputTokens + CompletionReserveTokens;
        var rounded = (desired + BudgetStepTokens - 1) / BudgetStepTokens * BudgetStepTokens;
        return (int)Math.Clamp(rounded, MinimumTokens, MaximumTokens);
    }

    /// <summary>
    /// Returns the visible-output budget plus capacity for hidden reasoning tokens.
    /// </summary>
    public static int CalculateWithReasoningReserve(string? systemPrompt, string? userText) =>
        checked(Calculate(systemPrompt, userText) + ReasoningReserveTokens);

    /// <summary>
    /// Caps an output budget to a local model's remaining context capacity.
    /// </summary>
    public static int FitToContext(
        int requestedOutputTokens,
        int promptTokenCount,
        int contextSize,
        string providerName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedOutputTokens, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(promptTokenCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(contextSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var remainingTokens = contextSize - promptTokenCount;
        if (remainingTokens <= 0)
        {
            throw new PluginRequestException(
                $"{providerName} cannot process this request because the prompt exceeds the model's context window.",
                PluginRequestFailureKind.RequestTooLarge,
                isTransient: false);
        }

        return Math.Min(requestedOutputTokens, remainingTokens);
    }
}
