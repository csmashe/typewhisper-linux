namespace TypeWhisper.Linux.Services;

/// <summary>Shares the retry allowance between a streaming request and its batch fallback.</summary>
public sealed class LlmRequestRetryBudget
{
    internal int RemainingAttempts { get; private set; } = 3;

    internal void RecordAttempt() => RemainingAttempts = Math.Max(0, RemainingAttempts - 1);
}
