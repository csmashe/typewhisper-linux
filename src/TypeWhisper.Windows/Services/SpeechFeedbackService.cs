using System.Diagnostics;
using System.Speech.Synthesis;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides spoken text-to-speech feedback for transcription events.
/// Uses System.Speech.Synthesis (SAPI) on Windows.
/// </summary>
public sealed class SpeechFeedbackService : IDisposable
{
    private SpeechSynthesizer? _synth;
    private bool _disposed;

    public bool IsEnabled { get; set; }

    public void Speak(string text, string? language = null)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(text)) return;

        try
        {
            EnsureSynthesizer();
            _synth!.SpeakAsyncCancelAll();
            _synth.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback error: {ex.Message}");
        }
    }

    public void AnnounceRecordingStarted() => Speak("Recording");

    public void AnnounceTranscriptionComplete(string text) => Speak(text);

    public void AnnounceError(string reason) => Speak($"Error: {reason}");

    public void Stop()
    {
        try { _synth?.SpeakAsyncCancelAll(); }
        catch { }
    }

    private void EnsureSynthesizer()
    {
        if (_synth is not null) return;
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _synth?.Dispose();
        _disposed = true;
    }
}
