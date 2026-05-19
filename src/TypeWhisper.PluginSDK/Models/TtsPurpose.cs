namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes why text-to-speech playback is being requested.
/// Providers may use this to adjust voice, speed, or volume for different contexts.
/// </summary>
public enum TtsPurpose
{
    /// <summary>Short status announcement (e.g. "Recording started").</summary>
    Status,
    /// <summary>Reading back the transcribed text after dictation.</summary>
    Transcription,
    /// <summary>User explicitly requested the text to be read aloud.</summary>
    ManualReadback
}
