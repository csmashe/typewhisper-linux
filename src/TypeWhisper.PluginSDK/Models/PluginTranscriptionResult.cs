// Non-"unused" inspections kept file-level: the 3-arg constructor is a deliberate back-compat
// overload (SDK < 1.1), and "json"/"verbose_json" in the docs are literal API values.
// ReSharper disable RedundantOverload.Global
// ReSharper disable GrammarMistakeInComment
// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Result of a transcription operation from a plugin engine.
/// </summary>
/// <param name="Text">The transcribed text.</param>
/// <param name="DetectedLanguage">ISO language code detected in the audio, or null.</param>
/// <param name="DurationSeconds">Duration of the audio in seconds.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginTranscriptionResult(
    string Text,
    string? DetectedLanguage,
    double DurationSeconds,
    float? NoSpeechProbability = null
)
{
    /// <summary>
    ///     Backward-compatible constructor for plugins compiled against SDK &lt; 1.1.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    // ReSharper disable once UnusedParameter.Global
    public PluginTranscriptionResult(string text, string detectedLanguage, double durationSeconds)
        : this(text, detectedLanguage, durationSeconds, null)
    {
    }

    /// <summary>Word/sentence segments from verbose_json responses, or empty for plain json.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public IReadOnlyList<PluginTranscriptionSegment> Segments { get; init; } = [];
}
