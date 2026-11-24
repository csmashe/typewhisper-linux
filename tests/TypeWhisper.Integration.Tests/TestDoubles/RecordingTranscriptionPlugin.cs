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
    private readonly ConcurrentQueue<bool> _receivedTranslate = new();
    private readonly ConcurrentQueue<string?> _receivedPrompts = new();
    private readonly ConcurrentQueue<string?> _streamingPrompts = new();

    internal IReadOnlyList<string?> ReceivedPrompts => [.. _receivedPrompts];

    internal IReadOnlyList<string?> StreamingPrompts => [.. _streamingPrompts];

    /// <summary>The session a stream opens; null keeps streaming unsupported, so it faults.</summary>
    internal ScriptedStreamingSession? StreamingSession { get; set; }

    internal Func<string?, bool>? StreamsPrompt { get; set; }

    internal Func<string?, bool>? PreviewsPrompt { get; set; }

    internal IReadOnlyList<bool> ReceivedTranslations => [.. _receivedTranslate];

    internal IReadOnlyList<string?> ReceivedLanguages => [.. _receivedLanguages];

    public string PluginId => Id;
    public string PluginName => "Integration scripted transcription";
    public string PluginVersion => "1.0.0";
    public string ProviderId { get; set; } = Id;
    public string ProviderDisplayName => "Scripted integration engine";
    public bool IsConfigured => true;
    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; set; } =
        [new(ModelId, "Scripted model")];
    public string? SelectedModelId { get; private set; } = ModelId;
    public bool SupportsTranslation { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsStructuredDictionaryTerms { get; set; }
    public DictionaryTermsBudget? DictionaryTermsBudget { get; set; }
    internal bool? ReceivedTranslate { get; private set; }
    public int TranscriptionCount => Volatile.Read(ref _transcriptionCount);
    public int LoadCount { get; private set; }
    public int UnloadCount { get; private set; }

    internal void EnqueueResult(Func<CancellationToken, Task<PluginTranscriptionResult>> result)
    {
        _results.Enqueue(result);
    }

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
        _receivedPrompts.Enqueue(prompt);
        ReceivedTranslate = translate;
        _receivedTranslate.Enqueue(translate);
        Interlocked.Increment(ref _transcriptionCount);
        if (!_results.TryDequeue(out var result))
        {
            return Task.FromException<PluginTranscriptionResult>(
                new InvalidOperationException("No scripted transcription result remains.")
            );
        }

        return result(ct);
    }

    public bool SupportsStreamingForPrompt(string? prompt) => StreamsPrompt?.Invoke(prompt) ?? SupportsStreaming;

    public bool SupportsStreamingPreviewForPrompt(string? prompt) => PreviewsPrompt?.Invoke(prompt) ?? false;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync([], null, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct
    ) => StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (StreamingSession is not { } session)
        {
            throw new NotSupportedException();
        }

        _streamingPrompts.Enqueue(prompt);
        return Task.FromResult<IStreamingSession>(session);
    }

    public void Dispose() { }
}

/// <summary>Answers the first audio chunk with one final segment and counts finalize requests.</summary>
internal sealed class ScriptedStreamingSession(string finalText) : IStreamingSession
{
    private int _finalizeCount;
    private int _sentAudio;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    internal TaskCompletionSource FirstAudio { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int FinalizeCount => Volatile.Read(ref _finalizeCount);

    internal bool Disposed { get; private set; }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _sentAudio, 1) != 0)
        {
            return Task.CompletedTask;
        }

        TranscriptReceived?.Invoke(new StreamingTranscriptEvent(finalText, true));
        FirstAudio.TrySetResult();
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _finalizeCount);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
