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
    ///     Restores volumes before the process exits: refuses later ducks and first waits up to
    ///     <paramref name="settleTimeout" /> for a duck still in flight, whose streams
    ///     <see cref="RestoreAudio" /> alone would only restore once that duck finished.
    /// </summary>
    void RestoreAudioForShutdown(TimeSpan settleTimeout);
}
