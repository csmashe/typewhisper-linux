// LoadCount/UnloadCount are the assertion surface for model lifecycle, kept public and paired
// so a test can assert either side without editing the double.
// ReSharper disable MemberCanBePrivate.Global
using System.Collections.Concurrent;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Integration.Tests.TestDoubles;

internal sealed class RecordingTranscriptionPlugin : ITranscriptionEnginePlugin
{
    internal const string Id = "integration.scripted-transcription";
    internal const string ModelId = "scripted-model";

    private readonly ConcurrentQueue<
        Func<CancellationToken, Task<PluginTranscriptionResult>>
    > _results = new();
    private readonly ConcurrentQueue<string?> _receivedLanguages = new();
    private int _transcriptionCount;

    internal IReadOnlyList<string?> ReceivedLanguages => [.. _receivedLanguages];

    public string PluginId => Id;
    public string PluginName => "Integration scripted transcription";
    public string PluginVersion => "1.0.0";
    public string ProviderId => Id;
    public string ProviderDisplayName => "Scripted integration engine";
    public bool IsConfigured => true;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [new(ModelId, "Scripted model")];
    public string? SelectedModelId { get; private set; } = ModelId;
    public bool SupportsTranslation => false;
    public int TranscriptionCount => Volatile.Read(ref _transcriptionCount);
    public int LoadCount { get; private set; }
    public int UnloadCount { get; private set; }

    internal void EnqueueText(string text, string? language = "en")
    {
        _results.Enqueue(
            _ => Task.FromResult(new PluginTranscriptionResult(text, language ?? "en", 1))
        );
    }

    internal void EnqueueFailure(string message)
    {
        _results.Enqueue(_ => Task.FromException<PluginTranscriptionResult>(
            new InvalidOperationException(message)
        ));
    }

    public Task ActivateAsync(IPluginHostServices host)
    {
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        return Task.CompletedTask;
    }

    public void SelectModel(string modelId)
    {
        SelectedModelId = modelId;
    }

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LoadCount++;
        SelectedModelId = modelId;
        return Task.CompletedTask;
    }

    public Task UnloadModelAsync()
    {
        UnloadCount++;
        SelectedModelId = null;
        return Task.CompletedTask;
    }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        _receivedLanguages.Enqueue(language);
        Interlocked.Increment(ref _transcriptionCount);
        if (!_results.TryDequeue(out var result))
        {
            return Task.FromException<PluginTranscriptionResult>(
                new InvalidOperationException("No scripted transcription result remains.")
            );
        }

        return result(ct);
    }

    public void Dispose() { }
}
