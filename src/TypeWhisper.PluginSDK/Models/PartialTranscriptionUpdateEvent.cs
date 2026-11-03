// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>Raised when partial transcription text is updated during recording.</summary>
// ReSharper disable once UnusedType.Global
public sealed record PartialTranscriptionUpdateEvent : PluginEvent, ICoalescibleEvent
{
    /// <summary>The current partial transcription text.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string PartialText { get; init; }

    /// <summary>Whether recording is still in progress.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool IsRecording { get; init; } = true;

    /// <inheritdoc />
    public bool IsTerminalFrame => !IsRecording;

    /// <summary>Elapsed seconds since recording started.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public double ElapsedSeconds { get; init; }
}
