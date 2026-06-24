namespace TypeWhisper.Core.Models;

/// <summary>
///     A recent transcription shown in the recent-transcriptions palette, sourced either from the
///     in-memory session store or from persisted history.
/// </summary>
public sealed record RecentTranscriptionEntry(
    string Id,
    string FinalText,
    DateTime Timestamp,
    string? AppName,
    string? AppProcessName,
    RecentTranscriptionSource Source
);
