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
    private readonly ModelManagerService _models;
    private readonly ISettingsService _settings;

    // Command execution and continuations that access this flag run on the UI thread.
    private bool _stopSaveInProgress;

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
    {
        _audio = audio;
        _models = models;
        _settings = settings;
        _audio.LevelChanged += (_, level) =>
            Dispatcher.UIThread.Post(() => AudioLevel = Math.Clamp(level * 8, 0, 1));
        LoadExistingRecordings();
    }

    public string RecordButtonText =>
        IsRecording ? Loc.Instance["Recorder.Stop"] : Loc.Instance["Recorder.Record"];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private void ToggleRecording()
    {
        if (_stopSaveInProgress)
        {
            return;
        }

        if (IsRecording)
        {
            SetStopSaveInProgress(true);
            _ = StopRecordingAsync();
        }
        else
        {
            StartRecording();
        }
    }

    private bool CanToggleRecording()
    {
        return !_stopSaveInProgress;
    }

    private void SetStopSaveInProgress(bool value)
    {
        if (_stopSaveInProgress == value)
        {
            return;
        }

        _stopSaveInProgress = value;
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    private void StartRecording()
    {
        _audio.StartRecording();
        if (!_audio.IsRecording)
        {
            StatusText = Loc.Instance["Recorder.StatusNoMicrophone"];
            return;
        }

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
        byte[] wav;
        string filePath;

        try
        {
            wav = await _audio.StopRecordingAsync();
            if (wav.Length == 0)
            {
                StatusText = Loc.Instance["Recorder.StatusNoAudio"];
                DurationText = "0:00";
                return;
            }

            // Off the dispatcher so a large WAV or slow disk doesn't freeze the UI;
            // CommitRecording touches no UI state.
            var recordingPath = TypeWhisperEnvironment.AudioPath;
            var wavBytes = wav;
            filePath = await Task.Run(
                () => RecorderFileNamer.CommitRecording(recordingPath, DateTime.Now, wavBytes)
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
            SetStopSaveInProgress(false);
        }

        var fileName = Path.GetFileName(filePath);

        StatusText = Loc.Instance["Recorder.StatusSavedTranscribing"];
        IsTranscribing = true;

        string? transcript;
        try
        {
            var effectiveModelId = _settings.Current.SelectedModelId;
            await using var lease = await _models.AcquireTranscriptionAsync(effectiveModelId);
            try
            {
                var result = await lease.Plugin.TranscribeAsync(
                    wav,
                    null,
                    false,
                    null,
                    CancellationToken.None
                );
                transcript = result.Text;
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
        catch
        {
            // Keep the recording even if transcription fails.
            transcript = null;
        }

        var transcriptWriteFailed = false;
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            try
            {
                AtomicFileWrite.WriteAllText(Path.ChangeExtension(filePath, ".txt"), transcript);
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
            : transcript is not null
                ? Loc.Instance["Recorder.StatusDone"]
                : Loc.Instance["Recorder.StatusSavedNoModel"];
        DurationText = "0:00";
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
            if (!Directory.Exists(TypeWhisperEnvironment.AudioPath))
            {
                return;
            }

            foreach (
                var file in Directory
                    .GetFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav")
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
