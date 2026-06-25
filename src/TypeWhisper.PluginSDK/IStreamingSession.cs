// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Represents an active real-time streaming transcription session (e.g. WebSocket connection).
///     Created by <see cref="ITranscriptionEnginePlugin.StartStreamingAsync" /> and fed audio by the host.
///     The host always calls <c>DisposeAsync</c>, even on cancellation or error paths — plugins must
///     tolerate disposal before <see cref="FinalizeAsync" /> completes.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IStreamingSession : IAsyncDisposable
{
    /// <summary>Sends a chunk of PCM16 mono 16 kHz audio to the streaming endpoint.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct);

    /// <summary>Signals end of audio input and flushes any remaining transcript.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task FinalizeAsync(CancellationToken ct);

    /// <summary>Fired when transcript text arrives. Raised from a background thread; marshal to UI as needed.</summary>
    // ReSharper disable once EventNeverSubscribedTo.Global
    event Action<StreamingTranscriptEvent> TranscriptReceived;
}

/// <summary>A transcript update from a streaming session.</summary>
/// <param name="Text">The transcript text (partial or final segment).</param>
/// <param name="IsFinal">True if this segment is confirmed and will not change.</param>
// ReSharper disable once UnusedType.Global
public sealed record StreamingTranscriptEvent(string Text, bool IsFinal);
