namespace TypeWhisper.Core.Models;

/// <summary>Lifecycle state of a transcription model, from not-yet-downloaded through ready (or errored).</summary>
public enum ModelStatusType
{
    NotDownloaded,
    Downloading,
    Loading,
    Ready,
    Error
}
