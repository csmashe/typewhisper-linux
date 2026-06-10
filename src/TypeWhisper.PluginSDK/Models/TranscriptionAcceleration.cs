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
///     Reports the acceleration backend the engine actually loaded with, plus a
///     restart-required flag for when the pinned runtime no longer matches the
///     user's saved preference.
/// </summary>
public sealed record TranscriptionAccelerationStatus(
    TranscriptionAccelerationBackend ActiveBackend,
    string DisplayText,
    string? Detail = null,
    bool RequiresRestart = false
);