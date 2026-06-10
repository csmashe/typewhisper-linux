namespace TypeWhisper.PluginSDK;

/// <summary>
///     Represents an active real-time streaming transcription session (e.g. WebSocket connection).
///     Created by <see cref="ITranscriptionEnginePlugin.StartStreamingAsync" /> and fed audio by the host.
///     The host always calls <c>DisposeAsync</c>, even on cancellation or error paths — plugins must
///     tolerate disposal before <see cref="FinalizeAsync" /> completes.
/// </summary>
public interface IStreamingSession : IAsyncDisposable
{
    /// <summary>Sends a chunk of PCM16 mono 16 kHz audio to the streaming endpoint.</summary>
    Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct);

    /// <summary>Signals end of audio input and flushes any remaining transcript.</summary>
    Task FinalizeAsync(CancellationToken ct);

    /// <summary>Fired when transcript text arrives. Raised from a background thread; marshal to UI as needed.</summary>
    event Action<StreamingTranscriptEvent> TranscriptReceived;
}

/// <summary>A transcript update from a streaming session.</summary>
/// <param name="Text">The transcript text (partial or final segment).</param>
/// <param name="IsFinal">True if this segment is confirmed and will not change.</param>
public sealed record StreamingTranscriptEvent(string Text, bool IsFinal);