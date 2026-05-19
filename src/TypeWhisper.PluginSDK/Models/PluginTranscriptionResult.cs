namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Result of a transcription operation from a plugin engine.
/// </summary>
/// <param name="Text">The transcribed text.</param>
/// <param name="DetectedLanguage">ISO language code detected in the audio, or null.</param>
/// <param name="DurationSeconds">Duration of the audio in seconds.</param>
public sealed record PluginTranscriptionResult(
    string Text, string? DetectedLanguage, double DurationSeconds,
    float? NoSpeechProbability = null)
{
    /// <summary>Word/sentence segments from verbose_json responses, or empty for plain json.</summary>
    public IReadOnlyList<PluginTranscriptionSegment> Segments { get; init; } = [];

    /// <summary>
    /// Backward-compatible constructor for plugins compiled against SDK &lt; 1.1.
    /// </summary>
    public PluginTranscriptionResult(string text, string detectedLanguage, double durationSeconds)
        : this(text, detectedLanguage, durationSeconds, null) { }
}

/// <summary>A timed text segment from a verbose transcription response.</summary>
/// <param name="Text">Segment text.</param>
/// <param name="Start">Start offset in seconds within the audio.</param>
/// <param name="End">End offset in seconds within the audio.</param>
public sealed record PluginTranscriptionSegment(string Text, double Start, double End);
