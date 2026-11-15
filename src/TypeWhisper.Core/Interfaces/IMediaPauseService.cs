namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Pauses currently-playing media when dictation starts and resumes it afterward,
///     so playback doesn't compete with the microphone.
/// </summary>
public interface IMediaPauseService
{
    void PauseMedia();
    void ResumeMedia();

    /// <summary>
    ///     Resumes media before the process exits, refusing later pauses. A pause still in
    ///     flight pauses no more players; this waits up to <paramref name="settleTimeout" /> for
    ///     its current command, then resumes every player paused so far before returning.
    /// </summary>
    void ResumeMediaForShutdown(TimeSpan settleTimeout);
}
