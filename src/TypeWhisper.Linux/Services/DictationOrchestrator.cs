using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Glues hotkey, recorder, transcription engine, and text injection into a
/// single dictation loop:
///   hotkey → start recording → hotkey → stop → save WAV → transcribe via
///   the active transcription plugin → xdotool types the result into the
///   focused window.
///
/// If no transcription plugin/model is loaded the WAV is still written so
/// the user can inspect what was captured.
/// </summary>
public sealed class DictationOrchestrator : IDisposable
{
    private readonly HotkeyService _hotkey;
    private readonly AudioRecordingService _audio;
    private readonly TextInsertionService _textInsertion;
    private readonly ModelManagerService _models;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler<string>? StatusMessage;

    public bool IsRecording => _audio.IsRecording;

    public DictationOrchestrator(
        HotkeyService hotkey,
        AudioRecordingService audio,
        TextInsertionService textInsertion,
        ModelManagerService models)
    {
        _hotkey = hotkey;
        _audio = audio;
        _textInsertion = textInsertion;
        _models = models;
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

                await TranscribeAndInsertAsync(wav);
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

    private async Task TranscribeAndInsertAsync(byte[] wav)
    {
        var plugin = _models.ActiveTranscriptionPlugin;
        if (plugin is null)
        {
            StatusMessage?.Invoke(this, "No transcription model loaded. WAV saved for review.");
            return;
        }

        StatusMessage?.Invoke(this, $"Transcribing via {plugin.ProviderDisplayName}…");
        try
        {
            var result = await plugin.TranscribeAsync(
                wavAudio: wav, language: null, translate: false,
                prompt: null, ct: CancellationToken.None);

            var text = result?.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                StatusMessage?.Invoke(this, "Transcription returned no text.");
                return;
            }

            TranscriptionCompleted?.Invoke(this, text);

            var insertion = await _textInsertion.InsertTextAsync(text);
            StatusMessage?.Invoke(this, insertion switch
            {
                InsertionResult.Pasted => $"Typed {text.Length} char(s).",
                InsertionResult.CopiedToClipboard => "Copied to clipboard (paste with Ctrl+V).",
                InsertionResult.Failed => "Text insertion failed — see logs.",
                _ => "Done.",
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Dictation] Transcription failed: {ex}");
            StatusMessage?.Invoke(this, $"Transcription failed: {ex.Message}");
        }
        finally
        {
            _models.ScheduleAutoUnload();
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
