using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public sealed record RecordingItem(string FileName, string FilePath, DateTime CreatedAt, TimeSpan Duration, string? Transcript);

public partial class RecorderSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly ModelManagerService _models;
    private readonly ISettingsService _settings;
    private System.Timers.Timer? _timer;
    private DateTime _recordingStart;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _audioLevel;
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _isTranscribing;

    public string RecordButtonText => IsRecording ? "Stop" : "Record";

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    public RecorderSectionViewModel(
        AudioRecordingService audio,
        ModelManagerService models,
        ISettingsService settings)
    {
        _audio = audio;
        _models = models;
        _settings = settings;
        _audio.LevelChanged += (_, level) => Dispatcher.UIThread.Post(() => AudioLevel = Math.Clamp(level * 8, 0, 1));
        LoadExistingRecordings();
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
            _ = StopRecordingAsync();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        _audio.StartRecording();
        if (!_audio.IsRecording)
        {
            StatusText = "No microphone available.";
            return;
        }

        IsRecording = true;
        OnPropertyChanged(nameof(RecordButtonText));
        _recordingStart = DateTime.UtcNow;
        StatusText = "Recording...";

        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _recordingStart;
            Dispatcher.UIThread.Post(() =>
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}");
        };
        _timer.Start();
    }

    private async Task StopRecordingAsync()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;

        var wav = _audio.StopRecording();
        IsRecording = false;
        OnPropertyChanged(nameof(RecordButtonText));
        AudioLevel = 0;
        var duration = DateTime.UtcNow - _recordingStart;

        if (wav.Length == 0)
        {
            StatusText = "No audio captured.";
            DurationText = "0:00";
            return;
        }

        var fileName = $"recording-{DateTime.Now:yyyy-MM-dd-HHmmss}.wav";
        var filePath = Path.Combine(TypeWhisperEnvironment.AudioPath, fileName);
        await File.WriteAllBytesAsync(filePath, wav);

        StatusText = "Saved. Transcribing...";
        IsTranscribing = true;

        string? transcript = null;
        try
        {
            var effectiveModelId = _settings.Current.SelectedModelId;
            await using var lease = await _models.AcquireTranscriptionAsync(effectiveModelId);
            try
            {
                var result = await lease.Plugin.TranscribeAsync(wav, null, false, null, CancellationToken.None);
                transcript = result.Text;
            }
            finally
            {
                // Native transcription is done — release _modelLock before the
                // transcript-to-disk write so a concurrent dictation isn't
                // blocked by it. The scope-end dispose is a harmless
                // idempotent no-op.
                await lease.DisposeAsync();
            }
            if (!string.IsNullOrWhiteSpace(transcript))
                await File.WriteAllTextAsync(Path.ChangeExtension(filePath, ".txt"), transcript);
        }
        catch
        {
            // Keep the recording even if transcription fails.
        }

        IsTranscribing = false;
        Recordings.Insert(0, new RecordingItem(fileName, filePath, DateTime.Now, duration, transcript));
        OnPropertyChanged(nameof(HasRecordings));
        StatusText = transcript is not null ? "Done." : "Saved (no model loaded)";
        DurationText = "0:00";
    }

    [RelayCommand]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null)
            return;

        try
        {
            if (File.Exists(item.FilePath))
                File.Delete(item.FilePath);

            var txtPath = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txtPath))
                File.Delete(txtPath);
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
                return;

            foreach (var file in Directory.GetFiles(TypeWhisperEnvironment.AudioPath, "recording-*.wav").OrderByDescending(path => path))
            {
                var info = new FileInfo(file);
                var txtFile = Path.ChangeExtension(file, ".txt");
                var transcript = File.Exists(txtFile) ? File.ReadAllText(txtFile) : null;
                Recordings.Add(new RecordingItem(info.Name, file, info.CreationTime, TimeSpan.Zero, transcript));
            }

            OnPropertyChanged(nameof(HasRecordings));
        }
        catch
        {
            // Ignore broken files.
        }
    }
}
