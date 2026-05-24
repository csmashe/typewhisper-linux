namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     User preference for transcription compute acceleration.
/// </summary>
public enum TranscriptionAccelerationPreference
{
    Auto,
    Cpu,
    NvidiaCuda
}

/// <summary>
///     A concrete compute backend a transcription engine can resolve to.
/// </summary>
public enum TranscriptionAccelerationBackend
{
    Cpu,
    NvidiaCuda
}

/// <summary>
///     Reports what acceleration the engine actually loaded with, along with a
///     human-readable display string and an optional restart-required signal that
///     surfaces when the engine's pinned runtime no longer matches the user's
///     saved preference.
/// </summary>
public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false
);
