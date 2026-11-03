namespace TypeWhisper.Core.Models;

/// <summary>
///     A timestamped span of transcribed text.
/// </summary>
/// <param name="Text">The transcribed text for this segment.</param>
/// <param name="Start">Offset in seconds from the start of the audio where this segment begins.</param>
/// <param name="End">Offset in seconds from the start of the audio where this segment ends.</param>
public sealed record TranscriptionSegment(string Text, double Start, double End)
{
    /// <summary>The segment's no-speech probability, or null when the engine does not report it.</summary>
    // ReSharper disable once UnusedAutoPropertyAccessor.Global -- carried for parity with the
    // plugin segment; exporters do not read it yet.
    public float? NoSpeechProbability { get; init; }
}
