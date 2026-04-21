using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Glues the hotkey, recorder, and text-insertion services into a single
/// dictation loop. v1 scope: hotkey toggles recording; on stop, the
/// captured WAV is written to disk for inspection and an event is raised
/// so future transcription pipeline can pick it up.
///
/// Full pipeline (recorded WAV → transcription plugin → text injection)
/// is stubbed — the transcription hook-in is a TODO until PluginManager
/// lands on Linux.
/// </summary>
public sealed class DictationOrchestrator : IDisposable
{
    private readonly HotkeyService _hotkey;
    private readonly AudioRecordingService _audio;
    private readonly TextInsertionService _textInsertion;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;

    public bool IsRecording => _audio.IsRecording;

    public DictationOrchestrator(
        HotkeyService hotkey,
        AudioRecordingService audio,
        TextInsertionService textInsertion)
    {
        _hotkey = hotkey;
        _audio = audio;
        _textInsertion = textInsertion;
    }

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _hotkey.DictationToggleRequested += (_, _) => _ = ToggleAsync();
        _hotkey.Initialize();
        _initialized = true;
    }

    public async Task ToggleAsync()
    {
        if (!await _toggleGate.WaitAsync(0)) return;
        try
        {
            if (_audio.IsRecording)
            {
                var wav = _audio.StopRecording();
                RecordingStateChanged?.Invoke(this, false);
                if (wav.Length == 0) return;

                var path = SaveWav(wav);
                RecordingCaptured?.Invoke(this, path);
                Debug.WriteLine($"[Dictation] Captured → {path} ({wav.Length} bytes)");
                // TODO(Phase1): run ITranscriptionEnginePlugin.TranscribeAsync here,
                // then _textInsertion.InsertTextAsync(result).
            }
            else
            {
                _audio.StartRecording();
                RecordingStateChanged?.Invoke(this, true);
            }
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    private static string SaveWav(byte[] wav)
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var name = $"dictation-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
        var path = Path.Combine(TypeWhisperEnvironment.AudioPath, name);
        File.WriteAllBytes(path, wav);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toggleGate.Dispose();
    }
}
