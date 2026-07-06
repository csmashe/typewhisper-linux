namespace TypeWhisper.Core.Models;

/// <summary>
///     Result of stripping trailing spoken commands from transcribed text: the cleaned
///     <see cref="Text" />, whether to press Enter after insertion, and whether to cancel
///     insertion entirely.
/// </summary>
public sealed record VoiceCommandParseResult(
    string Text,
    bool AutoEnter = false,
    bool CancelInsertion = false
);
