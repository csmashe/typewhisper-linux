namespace TypeWhisper.Core.Models;

/// <summary>
///     The raw output of a single transcription pass by a speech engine: the
///     recognized <see cref="Text" /> plus timing, detected language, optional
///     no-speech probability, and per-segment breakdown.
/// </summary>
public sealed record TranscriptionResult
{
    public required string Text { get; init; }
    public string? DetectedLanguage { get; init; }
    public double Duration { get; init; }
    public double ProcessingTime { get; init; }
    public float? NoSpeechProbability { get; init; }
    public IReadOnlyList<TranscriptionSegment> Segments { get; init; } = [];
}