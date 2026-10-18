using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>Looks up the per-app insertion strategy the user chose in Dictation settings.</summary>
internal static class AppInsertionStrategyResolver
{
    public static TextInsertionStrategy Resolve(
        IReadOnlyDictionary<string, TextInsertionStrategy>? strategies,
        string? processName
    )
    {
        // AppInsertionStrategies is JSON-deserialized and can be null when omitted.
        if (string.IsNullOrWhiteSpace(processName) || strategies is null || strategies.Count == 0)
        {
            return TextInsertionStrategy.Auto;
        }

        var process = ProcessNameNormalizer.Normalize(processName);
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop with dual-key (raw + normalized) matching is clearer than a LINQ rewrite.
        foreach (var entry in strategies)
        {
            if (
                string.Equals(entry.Key, processName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, process, StringComparison.OrdinalIgnoreCase)
            )
            {
                return entry.Value;
            }
        }

        return TextInsertionStrategy.Auto;
    }
}
