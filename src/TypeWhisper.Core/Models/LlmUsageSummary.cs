namespace TypeWhisper.Core.Models;

/// <summary>Which LLM produced a History entry's text and how long it took, from its <see cref="LlmCallProvenance" /> calls.</summary>
public readonly record struct LlmUsageSummary(string? Provider, string? Model, double? Seconds)
{
    /// <summary>Summarizes the cleanup, prompt-action and translation calls; memory extraction doesn't shape the text, so it is left out.</summary>
    public static LlmUsageSummary From(IReadOnlyList<LlmCallProvenance> calls)
    {
        var textCalls = calls.Where(call => call.Stage != "Memory").ToList();
        if (textCalls.Count == 0)
        {
            return default;
        }

        var pairs = textCalls.Select(call => (call.ProviderName, call.ModelId)).Distinct().ToList();
        var answered = textCalls.Where(call => call.DurationSeconds is not null).ToList();
        double? seconds = answered.Count == 0 ? null : answered.Sum(call => call.DurationSeconds!.Value);
        return pairs.Count == 1
            ? new LlmUsageSummary(pairs[0].ProviderName, pairs[0].ModelId, seconds)
            : new LlmUsageSummary(string.Join(", ", pairs.Select(pair => $"{pair.ProviderName} · {pair.ModelId}")), null, seconds);
    }
}
