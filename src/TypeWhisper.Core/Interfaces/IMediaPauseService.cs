namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Pauses currently-playing media when dictation starts and resumes it afterward,
///     so playback doesn't compete with the microphone.
/// </summary>
public interface IMediaPauseService
{
    void PauseMedia();
    void ResumeMedia();
}
