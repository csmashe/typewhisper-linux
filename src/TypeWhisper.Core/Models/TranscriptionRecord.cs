using System.Globalization;

// ReSharper disable UnusedAutoPropertyAccessor.Global
namespace TypeWhisper.Core.Models;

/// <summary>
///     A persisted history entry for one completed dictation: the raw and final
///     text, the app/URL/profile context it ran in, which engine and model
///     produced it, which processing steps were applied, and how the text was
///     inserted.
/// </summary>
public sealed record TranscriptionRecord
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string RawText { get; init; }
    public required string FinalText { get; init; }
    public string? AppName { get; init; }
    public string? AppProcessName { get; init; }
    public string? AppUrl { get; init; }
    public double DurationSeconds { get; init; }
    public string? Language { get; init; }
    public string? ProfileName { get; init; }
    public string EngineUsed { get; init; } = "whisper";
    public string? ModelUsed { get; init; }
    public string? AudioFileName { get; init; }
    public TextInsertionStatus InsertionStatus { get; init; } = TextInsertionStatus.Unknown;
    public string? InsertionFailureReason { get; init; }
    public CleanupLevel CleanupLevelUsed { get; init; } = CleanupLevel.None;
    public bool CleanupApplied { get; init; }
    public bool SnippetApplied { get; init; }
    public bool DictionaryCorrectionApplied { get; init; }
    public bool PromptActionApplied { get; init; }
    public bool TranslationApplied { get; init; }

    /// <summary>
    ///     True when this entry came from a spoken command (keyphrase mode) rather
    ///     than a plain dictation. Drives the "Command" badge in History; RawText is
    ///     then the source the command acted on and FinalText the produced result.
    /// </summary>
    public bool IsSpokenCommand { get; init; }
    public IReadOnlyList<CorrectionSuggestion> PendingCorrectionSuggestions { get; init; } = [];

    /// <summary>
    ///     Fine-grained provenance of each LLM call made while producing this
    ///     entry (cleanup and/or prompt action). Empty for pre-feature records
    ///     and for runs where provenance capture was disabled.
    /// </summary>
    public IReadOnlyList<LlmCallProvenance> LlmCalls { get; init; } = [];

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public int WordCount =>
        FinalText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public string Preview
    {
        get
        {
            const int maxLength = 100;

            if (FinalText.Length <= maxLength)
                return FinalText;

            // Only the code point starting at maxLength can affect a boundary at or
            // before maxLength — capping the scan there avoids an O(n) walk from many
            // combining marks.
            var scanLength = maxLength + 1;
            if (scanLength < FinalText.Length &&
                char.IsHighSurrogate(FinalText[maxLength]) &&
                char.IsLowSurrogate(FinalText[scanLength]))
                scanLength++;

            // Back off to the start of the last grapheme cluster beginning at or before
            // maxLength, so truncation never splits a cluster or a surrogate pair.
            var prefixLength = 0;
            var cursor = 0;
            while (cursor <= maxLength)
            {
                prefixLength = cursor;
                cursor += StringInfo.GetNextTextElementLength(FinalText.AsSpan(cursor, scanLength - cursor));
            }

            return string.Concat(FinalText.AsSpan(0, prefixLength), "...");
        }
    }
}
