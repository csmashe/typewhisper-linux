using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Applies the chosen regional spelling to final German output.</summary>
public static class GermanOutputNormalizationService
{
    /// <summary>Rewrites ß to ss for the Swiss variant when the resolved output language is German.</summary>
    public static string NormalizeText(
        string text,
        GermanOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? detectedLanguage = null,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null,
        string? translationTarget = null)
    {
        if (variant != GermanOutputVariant.Switzerland
            || string.IsNullOrEmpty(text)
            || !TranscriptionOutputLanguageResolver.IsOutputLanguage(
                "de",
                transcriptionTask,
                detectedLanguage,
                configuredLanguage,
                configuredLanguageCandidates ?? [],
                translationTarget))
        {
            return text;
        }

        return OutputLiteralTokens.RespellProse(
            text,
            static token => token
                .Replace("ẞ", "SS", StringComparison.Ordinal)
                .Replace("ß", "ss", StringComparison.Ordinal));
    }

    /// <summary>Respells a transcription's text and segments together.</summary>
    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        GermanOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null)
    {
        var candidates = configuredLanguageCandidates ?? [];
        return result with
        {
            Text = NormalizeText(
                result.Text,
                variant,
                transcriptionTask,
                result.DetectedLanguage,
                configuredLanguage,
                candidates),
            Segments = result.Segments
                .Select(segment => segment with
                {
                    Text = NormalizeText(
                        segment.Text,
                        variant,
                        transcriptionTask,
                        result.DetectedLanguage,
                        configuredLanguage,
                        candidates),
                })
                .ToList(),
        };
    }
}
