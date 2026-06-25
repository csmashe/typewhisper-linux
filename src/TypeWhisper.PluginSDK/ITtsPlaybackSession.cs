// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Represents an active text-to-speech playback session.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ITtsPlaybackSession
{
    /// <summary>Whether playback is still active.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    bool IsActive { get; }

    /// <summary>Stops playback if it is still active.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    void Stop();

    /// <summary>Raised when playback finishes or is stopped.</summary>
    // ReSharper disable once EventNeverSubscribedTo.Global
    event EventHandler? Completed;
}
