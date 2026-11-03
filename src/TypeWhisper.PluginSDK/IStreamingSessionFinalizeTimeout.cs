namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional for sessions whose provider confirms the transcript only after the end of audio.
///     The host waits up to this long for <see cref="IStreamingSession.FinalizeAsync" /> when
///     finalizing a dictation, clamped between its default and its own upper limit.
/// </summary>
public interface IStreamingSessionFinalizeTimeout
{
    TimeSpan FinalizeTimeout { get; }
}
