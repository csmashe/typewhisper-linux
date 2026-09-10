// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes why text-to-speech playback is being requested.
///     Providers may use this to adjust voice, speed, or volume for different contexts.
/// </summary>
// ReSharper disable once UnusedType.Global
public enum TtsPurpose
{
    /// <summary>Short status announcement (e.g. "Recording started").</summary>
    // ReSharper disable once UnusedMember.Global
    Status,

    /// <summary>Reading back the transcribed text after dictation.</summary>
    // ReSharper disable once UnusedMember.Global
    Transcription,

    /// <summary>User explicitly requested the text to be read aloud.</summary>
    // ReSharper disable once UnusedMember.Global
    ManualReadback,
}
