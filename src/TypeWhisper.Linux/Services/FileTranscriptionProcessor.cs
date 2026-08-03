using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public interface IFileTranscriptionProcessor
{
    Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken
    );
}

public sealed record FileTranscriptionProcessOptions(
    string? EngineId = null,
    string? ModelId = null,
    string? Language = null,
    TranscriptionTask? Task = null
);

public sealed record FileTranscriptionProcessProgress(
    FileTranscriptionQueueItemStatus Status,
    string StatusText
);

public sealed record FileTranscriptionProcessResult(
    TranscriptionResult RawResult,
    string ProcessedText
);

public sealed class FileTranscriptionProcessor(
    ModelManagerService modelManager,
    ISettingsService settings,
    AudioFileService audioFile,
    IDictionaryService dictionary,
    IVocabularyBoostingService vocabularyBoosting,
    IPostProcessingPipeline pipeline
) : IFileTranscriptionProcessor
{
    public async Task<FileTranscriptionProcessResult> ProcessAsync(
        string filePath,
        Action<FileTranscriptionProcessProgress> onProgress,
        FileTranscriptionProcessOptions? options,
        CancellationToken cancellationToken
    )
    {
        onProgress(
            new FileTranscriptionProcessProgress(
                FileTranscriptionQueueItemStatus.Loading,
                "Loading audio..."
            )
        );

        var modelId = ResolveModelId(options);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("No transcription model loaded.");
        }

        // Decode audio before acquiring the lease — ffmpeg shells out and must
        // not monopolize the global model lock while no transcription runs.
        var wav = await audioFile.LoadAudioAsWavAsync(filePath, cancellationToken);

        onProgress(
            new FileTranscriptionProcessProgress(
                FileTranscriptionQueueItemStatus.Transcribing,
                "Transcribing..."
            )
        );

        var currentSettings = settings.Current;
        var configuredLanguage = options?.Language ?? currentSettings.Language;
        var language = configuredLanguage == "auto" ? null : configuredLanguage;
        var task =
            options?.Task
            ?? (
                currentSettings.TranscriptionTask == "translate"
                    ? TranscriptionTask.Translate
                    : TranscriptionTask.Transcribe
            );

        var startedAt = DateTime.UtcNow;

        // Narrow the lease scope to TranscribeAsync only. Holding it through
        // the post-processing pipeline would block a concurrent dictation or
        // watch-folder transcription from loading a different model.
        PluginTranscriptionResult pluginResult;
        await using (
            var lease = await modelManager.AcquireTranscriptionAsync(
                modelId,
                cancellationToken: cancellationToken
            )
        )
        {
            pluginResult = await lease.Plugin.TranscribeAsync(
                wav,
                language,
                task == TranscriptionTask.Translate,
                null,
                cancellationToken
            );
        }

        var result = new TranscriptionResult
        {
            Text = pluginResult.Text,
            DetectedLanguage = pluginResult.DetectedLanguage,
            Duration = pluginResult.DurationSeconds,
            ProcessingTime = (DateTime.UtcNow - startedAt).TotalSeconds,
            NoSpeechProbability = pluginResult.NoSpeechProbability,
            Segments = pluginResult
                .Segments.Select(segment => new TranscriptionSegment(
                    segment.Text,
                    segment.Start,
                    segment.End
                ))
                .ToArray(),
        };

        var pipelineResult = await pipeline.ProcessAsync(
            result.Text,
            new PipelineOptions
            {
                VocabularyBooster = currentSettings.VocabularyBoostingEnabled
                    ? vocabularyBoosting.Apply
                    : null,
                DictionaryCorrector = dictionary.ApplyCorrections,
            },
            cancellationToken
        );

        return new FileTranscriptionProcessResult(result, pipelineResult.Text);
    }

    private string? ResolveModelId(FileTranscriptionProcessOptions? options)
    {
        if (
            !string.IsNullOrWhiteSpace(options?.ModelId)
            && ModelManagerService.IsPluginModel(options.ModelId)
        )
        {
            return options.ModelId;
        }

        if (!string.IsNullOrWhiteSpace(options?.EngineId))
        {
            var engine = modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ProviderId,
                    options.EngineId,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    candidate.PluginId,
                    options.EngineId,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (engine is null)
            {
                throw new InvalidOperationException(
                    $"Unknown transcription engine: {options.EngineId}"
                );
            }

            var model = string.IsNullOrWhiteSpace(options.ModelId)
                ? engine.SelectedModelId ?? (engine.TranscriptionModels.Count > 0 ? engine.TranscriptionModels[0] : null)?.Id
                : options.ModelId;
            if (
                string.IsNullOrWhiteSpace(model)
                || engine.TranscriptionModels.All(candidate => candidate.Id != model)
            )
            {
                throw new InvalidOperationException(
                    $"Unknown model for engine {options.EngineId}: {options.ModelId}"
                );
            }

            return ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model);
        }

        if (string.IsNullOrWhiteSpace(options?.ModelId))
        {
            return settings.Current.SelectedModelId;
        }

        // A bare model id is no longer globally unique: multiple engines/profiles
        // can advertise the same id. Don't silently route to the first match —
        // require the caller to disambiguate with an explicit engine.
        var matches = modelManager
            .PluginManager.TranscriptionEngines.Where(candidate =>
                candidate.TranscriptionModels.Any(model => model.Id == options.ModelId)
            )
            .ToList();
        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Unknown transcription model: {options.ModelId}"
            ),
            > 1 => throw new InvalidOperationException(
                $"Ambiguous transcription model '{options.ModelId}': provided by multiple engines. "
                    + "Specify the engine explicitly or use the full plugin-qualified model id."
            ),
            _ => ModelManagerService.GetPluginModelId(matches[0].GetTranscriptionSelectionId(), options.ModelId),
        };
    }
}
