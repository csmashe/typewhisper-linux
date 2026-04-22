using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class FileTranscriptionSectionViewModel : ObservableObject
{
    private readonly ModelManagerService _models;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFiles;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private CancellationTokenSource? _cts;
    private PluginTranscriptionResult? _lastResult;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = "Drag or select a file";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private bool _isDragOver;

    public bool CanImportFiles => _audioFiles.IsImporterAvailable;
    public bool ShowImporterUnavailableReason => !CanImportFiles;
    public string ImporterUnavailableReason => "Unavailable: ffmpeg is not installed on this system.";

    public FileTranscriptionSectionViewModel(
        ModelManagerService models,
        ISettingsService settings,
        AudioFileService audioFiles,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline)
    {
        _models = models;
        _settings = settings;
        _audioFiles = audioFiles;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _pipeline = pipeline;
    }

    [RelayCommand]
    private async Task TranscribeFile(string? path)
    {
        var filePath = path ?? FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        if (!AudioFileService.IsSupported(filePath))
        {
            StatusText = "Unsupported format";
            return;
        }

        FilePath = filePath;
        IsProcessing = true;
        HasResult = false;
        ResultText = "";
        DetectedLanguage = null;
        StatusText = "Loading audio...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var startedAt = DateTime.UtcNow;

        try
        {
            var effectiveModelId = _settings.Current.SelectedModelId;
            if (string.IsNullOrWhiteSpace(effectiveModelId))
            {
                StatusText = "No transcription model loaded.";
                return;
            }

            var loaded = await _models.EnsureModelLoadedAsync(effectiveModelId, _cts.Token);
            if (!loaded)
            {
                StatusText = "No transcription model loaded.";
                return;
            }

            var plugin = _models.ActiveTranscriptionPlugin;
            if (plugin is null)
            {
                StatusText = "No transcription model loaded.";
                return;
            }

            var wav = await _audioFiles.LoadAudioAsWavAsync(filePath, _cts.Token);
            StatusText = "Transcribing...";

            var language = _settings.Current.Language is { Length: > 0 } lang && lang != "auto"
                ? lang
                : null;
            var translate = string.Equals(_settings.Current.TranscriptionTask, "translate", StringComparison.OrdinalIgnoreCase);

            var result = await plugin.TranscribeAsync(wav, language, translate, null, _cts.Token);
            var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
            {
                VocabularyBooster = _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null,
                DictionaryCorrector = _dictionary.ApplyCorrections
            }, _cts.Token);

            _lastResult = result;
            ResultText = pipelineResult.Text;
            DetectedLanguage = result.DetectedLanguage;
            HasResult = true;

            var processingTime = (DateTime.UtcNow - startedAt).TotalSeconds;
            StatusText = $"Done in {processingTime:F1}s ({result.DurationSeconds:F1}s audio)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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

    public void HandleFileDrop(IReadOnlyList<string> files)
    {
        var file = files.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(file) && AudioFileService.IsSupported(file))
            TranscribeFileCommand.Execute(file);
    }

    public string BuildExportText() => ResultText;
}
