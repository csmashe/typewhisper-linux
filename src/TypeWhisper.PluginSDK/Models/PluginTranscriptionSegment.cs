// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>A timed text segment from a verbose transcription response.</summary>
/// <param name="Text">Segment text.</param>
/// <param name="Start">Start offset in seconds within the audio.</param>
/// <param name="End">End offset in seconds within the audio.</param>
// ReSharper disable once UnusedType.Global
public sealed record PluginTranscriptionSegment(string Text, double Start, double End);
