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
    ///     Resumes media before the process exits: refuses later pauses and first waits up to
    ///     <paramref name="settleTimeout" /> for a pause still in flight, whose players
    ///     <see cref="ResumeMedia" /> alone would only resume once that pause finished.
    /// </summary>
    void ResumeMediaForShutdown(TimeSpan settleTimeout);
}
