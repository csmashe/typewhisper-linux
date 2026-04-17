using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class FileTranscriptionViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;

    private CancellationTokenSource? _cts;
    private TranscriptionResult? _lastResult;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = Loc.Instance["FileTranscription.StatusDefault"];
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;

    public FileTranscriptionViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
    }

    [RelayCommand]
    private async Task TranscribeFile(string? path)
    {
        var filePath = path ?? FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        if (!AudioFileService.IsSupported(filePath))
        {
            StatusText = Loc.Instance["FileTranscription.UnsupportedFormat"];
            return;
        }

        FilePath = filePath;
        IsProcessing = true;
        HasResult = false;
        ResultText = "";
        StatusText = Loc.Instance["FileTranscription.LoadingAudio"];

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            if (!await _modelManager.EnsureModelLoadedAsync(cancellationToken: _cts.Token))
            {
                StatusText = Loc.Instance["Status.NoModelLoaded"];
                return;
            }

            var samples = await _audioFile.LoadAudioAsync(filePath, _cts.Token);

            StatusText = Loc.Instance["FileTranscription.Transcribing"];

            var s = _settings.Current;
            var language = s.Language == "auto" ? null : s.Language;
            var task = s.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;

            var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, _cts.Token);
            var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, _cts.Token);

            _lastResult = result;

            ResultText = pipelineResult.Text;
            DetectedLanguage = result.DetectedLanguage;
            ProcessingTime = result.ProcessingTime;
            AudioDuration = result.Duration;
            HasResult = true;
            StatusText = Loc.Instance.GetString("FileTranscription.DoneFormat", result.ProcessingTime, result.Duration);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc.Instance["Status.Cancelled"];
        }
        catch (Exception ex)
        {
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(ResultText))
            System.Windows.Clipboard.SetText(ResultText);
    }

    [RelayCommand]
    private void ExportSrt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("srt", SubtitleExporter.ToSrt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportWebVtt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("vtt", SubtitleExporter.ToWebVtt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportText()
    {
        if (string.IsNullOrEmpty(ResultText)) return;
        ExportFile("txt", ResultText);
    }

    private void ExportFile(string extension, string content)
    {
        var baseName = FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : "transcription";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}.{extension}",
            Filter = extension.ToUpperInvariant() + $" Files|*.{extension}|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, content);
            StatusText = Loc.Instance.GetString("FileTranscription.ExportedFormat", Path.GetFileName(dialog.FileName));
        }
    }

    public void HandleFileDrop(string[] files)
    {
        if (files.Length > 0 && AudioFileService.IsSupported(files[0]))
        {
            TranscribeFileCommand.Execute(files[0]);
        }
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;
}
