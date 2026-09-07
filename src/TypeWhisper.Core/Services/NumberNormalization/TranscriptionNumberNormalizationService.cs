using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.NumberNormalization;

public static class TranscriptionNumberNormalizationService
{
    // ReSharper disable once MemberCanBePrivate.Global -- public API of the service, so callers can
    // check the toggle without running a normalization pass.
    public static bool IsEnabled(bool globalEnabled = true, bool? normalizeNumbersOverride = null) =>
        normalizeNumbersOverride ?? globalEnabled;

    public static string NormalizeText(
        string text,
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null) =>
        IsEnabled(globalEnabled, normalizeNumbersOverride)
            ? NormalizeText(
                text,
                NormalizationLanguages(
                    transcriptionTask,
                    detectedLanguage,
                    configuredLanguage,
                    configuredLanguageCandidates),
                globalEnabled,
                normalizeNumbersOverride)
            : text;

    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        TranscriptionTask transcriptionTask,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null)
    {
        var languages = NormalizationLanguages(
            transcriptionTask,
            result.DetectedLanguage,
            configuredLanguage,
            configuredLanguageCandidates);

        return result with
        {
            Text = NormalizeText(
                result.Text,
                languages,
                globalEnabled,
                normalizeNumbersOverride),
            Segments = NormalizeSegments(
                result.Segments,
                languages,
                globalEnabled,
                normalizeNumbersOverride),
        };
    }

    // ReSharper disable once UnusedMember.Global -- segment-level counterpart of NormalizeText and
    // NormalizeResult; part of the public API even though callers currently only need the other two.
    public static IReadOnlyList<TranscriptionSegment> NormalizeSegments(
        IReadOnlyList<TranscriptionSegment> segments,
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null)
    {
        var languages = NormalizationLanguages(
            transcriptionTask,
            detectedLanguage,
            configuredLanguage,
            configuredLanguageCandidates);

        return NormalizeSegments(segments, languages, globalEnabled, normalizeNumbersOverride);
    }

    private static List<string> NormalizationLanguages(
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates)
    {
        if (transcriptionTask == TranscriptionTask.Translate)
            return ["en"];

        return PrioritizedLanguages(
            detectedLanguage,
            [.. new[] { configuredLanguage }.Where(static language => language is not null).Select(static language => language!), .. configuredLanguageCandidates]);
    }

    private static string NormalizeText(
        string text,
        IReadOnlyList<string> languages,
        bool globalEnabled,
        bool? normalizeNumbersOverride)
    {
        if (!IsEnabled(globalEnabled, normalizeNumbersOverride))
            return text;

        foreach (var language in languages)
        {
            var normalized = NumberWordNormalizer.Normalize(text, language);
            if (!string.Equals(normalized, text, StringComparison.Ordinal))
                return normalized;
        }

        return text;
    }

    private static List<TranscriptionSegment> NormalizeSegments(
        IReadOnlyList<TranscriptionSegment> segments,
        IReadOnlyList<string> languages,
        bool globalEnabled,
        bool? normalizeNumbersOverride) =>
        segments
            .Select(segment => segment with
            {
                Text = NormalizeText(segment.Text, languages, globalEnabled, normalizeNumbersOverride),
            })
            .ToList();

    private static List<string> PrioritizedLanguages(string? primary, IReadOnlyList<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // ReSharper disable once LoopCanBeConvertedToQuery -- the dedupe is the HashSet side effect;
        // a query would hide the mutating seen.Add inside a where-clause.
        foreach (var rawLanguage in new[] { primary }.Where(static language => language is not null).Select(static language => language!).Concat(candidates))
        {
            var normalized = NumberWordNormalizer.NormalizeLanguageCode(rawLanguage);
            if (normalized is null || !seen.Add(normalized))
                continue;

            result.Add(normalized);
        }

        return result;
    }
}
