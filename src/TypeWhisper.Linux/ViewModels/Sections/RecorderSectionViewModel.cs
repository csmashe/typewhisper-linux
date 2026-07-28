using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Timer = System.Timers.Timer;

namespace TypeWhisper.Linux.ViewModels.Sections;

public sealed record RecordingItem(
    string FileName,
    string FilePath,
    DateTime CreatedAt,
    TimeSpan Duration,
    string? Transcript
);

public partial class RecorderSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly string _recordingDirectory;
    private readonly ISettingsService _settings;
    private readonly Func<byte[], Task<string?>> _transcribeAsync;
    private AudioRecordingService.AudioCaptureSession? _captureSession;

    [ObservableProperty]
    private double _audioLevel;

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTranscribing;

    private DateTime _recordingStart;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Recorder.StatusReady"];

    private Timer? _timer;

    public RecorderSectionViewModel(
        AudioRecordingService audio,
        ModelManagerService models,
        ISettingsService settings
    )
        : this(
            audio,
            settings,
            TypeWhisperEnvironment.AudioPath,
            CreateTranscriptionDelegate(models, settings)
        )
    {
    }

    internal RecorderSectionViewModel(
        AudioRecordingService audio,
        ISettingsService settings,
        string recordingDirectory,
        Func<byte[], Task<string?>> transcribeAsync
    )
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingDirectory);
        ArgumentNullException.ThrowIfNull(transcribeAsync);

        _audio = audio;
        _settings = settings;
        _recordingDirectory = recordingDirectory;
        _transcribeAsync = transcribeAsync;
        _audio.LevelChanged += (_, level) =>
        {
            if (IsRecording)
            {
                AudioLevel = Math.Clamp(level * 8, 0, 1);
            }
        };
        LoadExistingRecordings();
    }

    public string RecordButtonText =>
        IsRecording ? Loc.Instance["Recorder.Stop"] : Loc.Instance["Recorder.Record"];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    [RelayCommand]
    private async Task ToggleRecording()
    {
        if (IsRecording)
        {
            await StopRecordingAsync();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        var captureSession = _audio.TryStartRecording(_settings.Current.WhisperModeEnabled);
        if (captureSession is null)
        {
            StatusText = Loc.Instance["Recorder.StatusNoMicrophone"];
            return;
        }

        _captureSession = captureSession;
        IsRecording = true;
        OnPropertyChanged(nameof(RecordButtonText));
        _recordingStart = DateTime.UtcNow;
        StatusText = Loc.Instance["Recorder.StatusRecording"];

        _timer = new Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _recordingStart;
            Dispatcher.UIThread.Post(() =>
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}"
            );
        };
        _timer.Start();
    }

    private async Task StopRecordingAsync()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;

        var duration = DateTime.UtcNow - _recordingStart;
        var captureSession = _captureSession;
        _captureSession = null;
        byte[] wav;
        string filePath;

        try
        {
            wav = captureSession is null
                ? []
                : await _audio.StopRecordingAsync(captureSession);
            if (wav.Length == 0)
            {
                StatusText = Loc.Instance["Recorder.StatusNoAudio"];
                DurationText = "0:00";
                return;
            }

            // Off the dispatcher so a large WAV or slow disk doesn't freeze the UI;
            // CommitRecording touches no UI state.
            var wavBytes = wav;
            filePath = await Task.Run(
                () => RecorderFileNamer.CommitRecording(_recordingDirectory, DateTime.Now, wavBytes)
            );
        }
        catch
        {
            StatusText = Loc.Instance["Recorder.StatusSaveFailed"];
            DurationText = "0:00";
            return;
        }
        finally
        {
            IsRecording = false;
            OnPropertyChanged(nameof(RecordButtonText));
            AudioLevel = 0;
        }

        var fileName = Path.GetFileName(filePath);

        StatusText = Loc.Instance["Recorder.StatusSavedTranscribing"];
        IsTranscribing = true;

        string? transcript;
        try
        {
            transcript = await _transcribeAsync(wav);
        }
        catch
        {
            // Keep the recording even if transcription fails.
            transcript = null;
        }

        var transcriptWriteFailed = false;
        var transcriptPersisted = false;
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            try
            {
                AtomicFileWrite.WriteAllTextCreateNew(
                    Path.ChangeExtension(filePath, ".txt"),
                    transcript
                );
                transcriptPersisted = true;
            }
            catch
            {
                // Keep the recording; report the sidecar failure instead of
                // misreporting "no model loaded".
                transcriptWriteFailed = true;
            }
        }

        IsTranscribing = false;
        Recordings.Insert(
            0,
            new RecordingItem(fileName, filePath, DateTime.Now, duration, transcript)
        );
        OnPropertyChanged(nameof(HasRecordings));
        StatusText = transcriptWriteFailed
            ? Loc.Instance["Recorder.StatusTranscriptSaveFailed"]
            : transcriptPersisted
                ? Loc.Instance["Recorder.StatusDone"]
                : Loc.Instance["Recorder.StatusSavedNoModel"];
        DurationText = "0:00";
    }

    private static Func<byte[], Task<string?>> CreateTranscriptionDelegate(
        ModelManagerService models,
        ISettingsService settings
    )
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        return wav => TranscribeAsync(models, settings, wav);
    }

    private static async Task<string?> TranscribeAsync(
        ModelManagerService models,
        ISettingsService settings,
        byte[] wav
    )
    {
        var effectiveModelId = settings.Current.SelectedModelId;
        await using var lease = await models.AcquireTranscriptionAsync(effectiveModelId);
        try
        {
            var result = await lease.Plugin.TranscribeAsync(
                wav,
                null,
                false,
                null,
                CancellationToken.None
            );
            return result.Text;
        }
        finally
        {
            // Release the model lock before writing to disk so a concurrent
            // dictation isn't blocked by the file I/O that follows.
            // The using-statement above will call DisposeAsync again on
            // exit, but the lease is idempotent so the double-dispose is safe.
            // ReSharper disable once DisposeOnUsingVariable -- intentional early release of the model lock before the file I/O below.
            await lease.DisposeAsync();
        }
    }

    [RelayCommand]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (File.Exists(item.FilePath))
            {
                File.Delete(item.FilePath);
            }

            var txtPath = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txtPath))
            {
                File.Delete(txtPath);
            }
        }
        catch
        {
            // Best effort.
        }

        Recordings.Remove(item);
        OnPropertyChanged(nameof(HasRecordings));
    }

    private void LoadExistingRecordings()
    {
        try
        {
            if (!Directory.Exists(_recordingDirectory))
            {
                return;
            }

            foreach (
                var file in Directory
                    .GetFiles(_recordingDirectory, "recording-*.wav")
                    .OrderByDescending(path => path)
            )
            {
                var info = new FileInfo(file);
                var txtFile = Path.ChangeExtension(file, ".txt");
                var transcript = File.Exists(txtFile) ? File.ReadAllText(txtFile) : null;
                Recordings.Add(
                    new RecordingItem(info.Name, file, info.CreationTime, TimeSpan.Zero, transcript)
                );
            }

            OnPropertyChanged(nameof(HasRecordings));
        }
        catch
        {
            // Ignore broken files.
        }
    }
}
