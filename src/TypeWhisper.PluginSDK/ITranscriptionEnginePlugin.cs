using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides audio transcription capabilities via a cloud or local engine.
/// </summary>
public interface ITranscriptionEnginePlugin : ITypeWhisperPlugin
{
    /// <summary>Unique provider identifier (e.g. "openai", "groq").</summary>
    string ProviderId { get; }

    /// <summary>Human-readable provider name for the UI.</summary>
    string ProviderDisplayName { get; }

    /// <summary>Whether the provider is configured and ready (API key set, etc.).</summary>
    bool IsConfigured { get; }

    /// <summary>Available transcription models for this provider.</summary>
    IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }

    /// <summary>Currently selected model ID, or null if none selected.</summary>
    string? SelectedModelId { get; }

    /// <summary>Whether this provider supports translation (audio to English).</summary>
    bool SupportsTranslation { get; }

    /// <summary>Whether this engine supports downloading and managing local model files.</summary>
    bool SupportsModelDownload => false;

    /// <summary>Whether this engine supports real-time streaming transcription via <see cref="IStreamingSession" />.</summary>
    bool SupportsStreaming => false;

    /// <summary>ISO language codes supported by this engine, or empty for all.</summary>
    IReadOnlyList<string> SupportedLanguages => [];

    /// <summary>Selects a transcription model by ID.</summary>
    void SelectModel(string modelId);

    /// <summary>Configures the preferred compute backend. Common values: "cpu", "cuda".</summary>
    void ConfigureComputeBackend(string backend) { }

    /// <summary>Acceleration backends this engine can run on. Default: CPU only.</summary>
    IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends =>
        [TranscriptionAccelerationBackend.Cpu];

    /// <summary>Acceleration preference last requested by the host. Default: Auto.</summary>
    TranscriptionAccelerationPreference AccelerationPreference =>
        TranscriptionAccelerationPreference.Auto;

    /// <summary>Reports what acceleration the engine actually loaded with.</summary>
    TranscriptionAccelerationStatus AccelerationStatus =>
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    /// <summary>
    ///     Sets the host's resolved acceleration preference. The host resolves <c>Auto</c> to a
    ///     concrete backend before calling, so plugins only see <c>Cpu</c> or <c>NvidiaCuda</c>
    ///     in practice.
    /// </summary>
    void SetAccelerationPreference(TranscriptionAccelerationPreference preference) { }

    /// <summary>Transcribes WAV audio data and returns the result.</summary>
    Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    );

    /// <summary>Whether the given model's files are downloaded and ready to use.</summary>
    bool IsModelDownloaded(string modelId)
    {
        return true;
    }

    /// <summary>Downloads model files for the given model ID, reporting progress 0.0–1.0.</summary>
    Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>Loads a downloaded model into memory, preparing it for transcription.</summary>
    Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>Deletes downloaded model files for the given model ID.</summary>
    Task DeleteModelAsync(string modelId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Opens a real-time streaming session (e.g. WebSocket). The host feeds PCM16 audio via the session.
    ///     Only called when <see cref="SupportsStreaming" /> is true.
    /// </summary>
    Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
    {
        throw new NotSupportedException();
    }

    /// <summary>Unloads the currently loaded model from memory to free resources.</summary>
    Task UnloadModelAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Transcribes audio with streaming progress updates. Default delegates to <see cref="TranscribeAsync" />.
    /// </summary>
    /// <param name="wavAudio">WAV audio data to transcribe.</param>
    /// <param name="language">Target language code, or null for auto-detect.</param>
    /// <param name="translate">Whether to translate the result to English.</param>
    /// <param name="prompt">Optional prompt/context hint for the engine.</param>
    /// <param name="onProgress">Callback invoked with partial transcription text. Return false to cancel.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        Func<string, bool> onProgress,
        CancellationToken ct
    )
    {
        return TranscribeAsync(wavAudio, language, translate, prompt, ct);
    }
}