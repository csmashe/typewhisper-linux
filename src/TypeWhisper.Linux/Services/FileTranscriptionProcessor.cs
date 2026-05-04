using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Services;

public interface IFileTranscriptionProcessor
{
    Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken);
}

public sealed record FileTranscriptionProcessOptions(
    string? EngineId = null,
    string? ModelId = null,
    string? Language = null,
    TranscriptionTask? Task = null);

public sealed record FileTranscriptionProcessProgress(
    FileTranscriptionQueueItemStatus Status,
    string StatusText);

public sealed record FileTranscriptionProcessResult(
    TranscriptionResult RawResult,
    string ProcessedText);

public sealed class FileTranscriptionProcessor(
    ModelManagerService modelManager,
    ISettingsService settings,
    AudioFileService audioFile,
    IDictionaryService dictionary,
    IVocabularyBoostingService vocabularyBoosting,
    IPostProcessingPipeline pipeline) : IFileTranscriptionProcessor
{
    public async Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken)
    {
        onProgress(new FileTranscriptionProcessProgress(
            FileTranscriptionQueueItemStatus.Loading,
            "Loading audio..."));

        var modelId = ResolveModelId(options);
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("No transcription model loaded.");

        if (!await modelManager.EnsureModelLoadedAsync(modelId, cancellationToken))
            throw new InvalidOperationException("No transcription model loaded.");

        var plugin = modelManager.ActiveTranscriptionPlugin
            ?? throw new InvalidOperationException("No transcription engine loaded.");

        var wav = await audioFile.LoadAudioAsWavAsync(filePath, cancellationToken);

        onProgress(new FileTranscriptionProcessProgress(
            FileTranscriptionQueueItemStatus.Transcribing,
            "Transcribing..."));

        var currentSettings = settings.Current;
        var configuredLanguage = options?.Language ?? currentSettings.Language;
        var language = configuredLanguage == "auto" ? null : configuredLanguage;
        var task = options?.Task ?? (currentSettings.TranscriptionTask == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe);

        var startedAt = DateTime.UtcNow;
        var pluginResult = await plugin.TranscribeAsync(
            wav,
            language,
            task == TranscriptionTask.Translate,
            prompt: null,
            cancellationToken);

        var result = new TranscriptionResult
        {
            Text = pluginResult.Text,
            DetectedLanguage = pluginResult.DetectedLanguage,
            Duration = pluginResult.DurationSeconds,
            ProcessingTime = (DateTime.UtcNow - startedAt).TotalSeconds,
            NoSpeechProbability = pluginResult.NoSpeechProbability,
            Segments = pluginResult.Segments
                .Select(segment => new TranscriptionSegment(segment.Text, segment.Start, segment.End))
                .ToArray()
        };

        var pipelineResult = await pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            VocabularyBooster = currentSettings.VocabularyBoostingEnabled ? vocabularyBoosting.Apply : null,
            DictionaryCorrector = dictionary.ApplyCorrections
        }, cancellationToken);

        modelManager.ScheduleAutoUnload();

        return new FileTranscriptionProcessResult(result, pipelineResult.Text);
    }

    private string? ResolveModelId(FileTranscriptionProcessOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.ModelId) && ModelManagerService.IsPluginModel(options.ModelId))
            return options.ModelId;

        if (!string.IsNullOrWhiteSpace(options?.EngineId))
        {
            var engine = modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, options.EngineId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.PluginId, options.EngineId, StringComparison.OrdinalIgnoreCase));
            if (engine is null)
                throw new InvalidOperationException($"Unknown transcription engine: {options.EngineId}");

            var model = string.IsNullOrWhiteSpace(options.ModelId)
                ? engine.SelectedModelId ?? engine.TranscriptionModels.FirstOrDefault()?.Id
                : options.ModelId;
            if (string.IsNullOrWhiteSpace(model) || engine.TranscriptionModels.All(candidate => candidate.Id != model))
                throw new InvalidOperationException($"Unknown model for engine {options.EngineId}: {options.ModelId}");

            return ModelManagerService.GetPluginModelId(engine.PluginId, model);
        }

        if (!string.IsNullOrWhiteSpace(options?.ModelId))
        {
            var engine = modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
                candidate.TranscriptionModels.Any(model => model.Id == options.ModelId));
            if (engine is null)
                throw new InvalidOperationException($"Unknown transcription model: {options.ModelId}");

            return ModelManagerService.GetPluginModelId(engine.PluginId, options.ModelId);
        }

        return settings.Current.SelectedModelId;
    }
}
