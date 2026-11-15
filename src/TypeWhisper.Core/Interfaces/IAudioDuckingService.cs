namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Temporarily lowers other applications' playback volume during dictation so
///     audio doesn't bleed into the microphone capture, then restores it afterward.
/// </summary>
public interface IAudioDuckingService
{
    /// <summary>Scales active output volumes by <paramref name="factor" /> of their current level (e.g. 0.2 = 20%).</summary>
    void DuckAudio(float factor);

    /// <summary>Restores volumes to the levels captured before the last <see cref="DuckAudio" /> call.</summary>
    void RestoreAudio();

    /// <summary>
    ///     Restores volumes before the process exits, refusing later ducks. A duck still in
    ///     flight lowers no more streams; this waits up to <paramref name="settleTimeout" /> for
    ///     its current command to exit, then restores every stream lowered so far before
    ///     returning. A command that outlasts the wait is undone by the duck once it exits.
    /// </summary>
    void RestoreAudioForShutdown(TimeSpan settleTimeout);
}
