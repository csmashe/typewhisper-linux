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

    public string Preview =>
        FinalText.Length > 100 ? string.Concat(FinalText.AsSpan(0, 100), "...") : FinalText;
}