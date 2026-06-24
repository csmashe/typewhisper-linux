namespace TypeWhisper.Core.Models;

/// <summary>
///     A timestamped span of transcribed text; <paramref name="Start" /> and
///     <paramref name="End" /> are offsets in seconds from the start of the audio.
/// </summary>
public sealed record TranscriptionSegment(string Text, double Start, double End);