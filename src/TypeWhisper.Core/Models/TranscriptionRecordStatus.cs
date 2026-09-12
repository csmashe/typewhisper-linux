namespace TypeWhisper.Core.Models;

/// <summary>
///     Outcome of a dictation run as recorded in the history: it produced usable text, transcription
///     itself failed, or post-processing failed after transcription returned raw text.
///     Persisted by numeric value in history.json — append new members; never reorder these ordinals.
/// </summary>
public enum TranscriptionRecordStatus
{
    Succeeded = 0,
    TranscriptionFailed = 1,
    ProcessingFailed = 2,
}
