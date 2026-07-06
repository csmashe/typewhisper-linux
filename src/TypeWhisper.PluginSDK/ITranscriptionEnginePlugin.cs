// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides audio transcription capabilities via a cloud or local engine.
/// </summary>
// ReSharper disable once UnusedType.Global
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

    /// <summary>Acceleration backends this engine can run on. Default: CPU only.</summary>
    IReadOnlyList<TranscriptionAccelerationBackend> SupportedAccelerationBackends =>
        [TranscriptionAccelerationBackend.Cpu];

    /// <summary>
    ///     Whether this engine downloads and preloads its own CUDA runtime on demand
    ///     during <see cref="LoadModelAsync(string, CancellationToken)" />, and falls back to CPU itself
    ///     (surfacing the reason via <see cref="AccelerationStatus" />) when the GPU
    ///     path can't be honored. When <c>true</c>, the host must not reject an
    ///     explicit <see cref="TranscriptionAccelerationBackend.NvidiaCuda" /> load
    ///     just because the CUDA runtime libraries aren't already installed on the
    ///     host — the plugin provisions them. Default: <c>false</c> (the engine relies
    ///     on a host-provided CUDA runtime).
    /// </summary>
    bool ProvisionsCudaRuntimeOnDemand => false;

    /// <summary>
    ///     For a self-provisioning engine (<see cref="ProvisionsCudaRuntimeOnDemand" />),
    ///     whether the CUDA runtime it needs is already fully available — every
    ///     required library either provided by the host system or already downloaded
    ///     into the cache. Pure inspection (no driver probe, no download), so the host
    ///     can poll it to decide whether CUDA can be selected now (<c>true</c>) or the
    ///     runtime still needs fetching (<c>false</c>, including the partial-install
    ///     case where only some libraries are present). Default: <c>false</c>.
    /// </summary>
    bool IsCudaRuntimeProvisioned => false;

    /// <summary>
    ///     Downloads and preloads only the CUDA runtime libraries this engine is still
    ///     missing (a no-op when <see cref="IsCudaRuntimeProvisioned" /> is already
    ///     <c>true</c>), reporting progress 0.0–1.0. Lets the host offer an explicit
    ///     "download CUDA runtime" action on a driver-only host instead of waiting for
    ///     the lazy <see cref="LoadModelAsync(string, IProgress{double}, CancellationToken)" />
    ///     path. Throws if the NVIDIA driver is unusable or the download fails — the
    ///     host surfaces the message. Default: no-op (engines that rely on a
    ///     host-provided runtime have nothing to fetch).
    /// </summary>
    // ReSharper disable UnusedParameter.Global
    Task EnsureCudaRuntimeReadyAsync(IProgress<double>? progress, CancellationToken ct)
        // ReSharper restore UnusedParameter.Global
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Deletes this engine's provisioned CUDA runtime caches (the shared CUDA math
    ///     libraries plus any per-engine GPU build) so the next CUDA load re-provisions
    ///     from scratch. Best-effort. Note: libraries already dlopen'd this session are
    ///     held until process exit, so a restart is required for a fresh re-download to
    ///     take effect. Default: no-op for engines that rely on a host-provided runtime
    ///     (nothing to clear); a self-provisioning engine
    ///     (<see cref="ProvisionsCudaRuntimeOnDemand" />) MUST override this — the default
    ///     throws rather than silently report a clear that never happened (which would
    ///     leave a corrupt cache in place and defeat the host's failure aggregation).
    /// </summary>
    // ReSharper disable once UnusedParameter.Global
    Task ClearCudaRuntimeAsync(CancellationToken ct)
    {
        if (ProvisionsCudaRuntimeOnDemand)
        {
            throw new NotSupportedException(
                $"{ProviderId} provisions its CUDA runtime on demand and must override "
                    + $"{nameof(ClearCudaRuntimeAsync)}."
            );
        }

        return Task.CompletedTask;
    }

    /// <summary>Acceleration preference last requested by the host. Default: Auto.</summary>
    // ReSharper disable once UnusedMember.Global
    TranscriptionAccelerationPreference AccelerationPreference =>
        TranscriptionAccelerationPreference.Auto;

    /// <summary>Reports what acceleration the engine actually loaded with.</summary>
    TranscriptionAccelerationStatus AccelerationStatus =>
        new(TranscriptionAccelerationBackend.Cpu, "Using CPU");

    /// <summary>Selects a transcription model by ID.</summary>
    void SelectModel(string modelId);

    /// <summary>Configures the preferred compute backend. Common values: "cpu", "cuda".</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    Task ConfigureComputeBackendAsync(string backend)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Sets the resolved acceleration preference. The host resolves <c>Auto</c>
    ///     before calling, so plugins only ever see <c>Cpu</c> or <c>NvidiaCuda</c>.
    /// </summary>
    void SetAccelerationPreference(TranscriptionAccelerationPreference preference) { }

    /// <summary>Transcribes WAV audio data and returns the result.</summary>
    Task<PluginTranscriptionResult> TranscribeAsync(
        // ReSharper disable UnusedParameter.Global
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
        // ReSharper restore UnusedParameter.Global
    );

    /// <summary>Whether the given model's files are downloaded and ready to use.</summary>
    // ReSharper disable once UnusedParameter.Global
    bool IsModelDownloaded(string modelId)
    {
        return true;
    }

    /// <summary>Downloads model files for the given model ID, reporting progress 0.0–1.0.</summary>
    // ReSharper disable UnusedParameter.Global
    Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        // ReSharper restore UnusedParameter.Global
    {
        return Task.CompletedTask;
    }

    /// <summary>Loads a downloaded model into memory, preparing it for transcription.</summary>
    Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Loads a downloaded model into memory, reporting provisioning/download
    ///     progress 0.0–1.0 via <paramref name="progress" /> — e.g. when a
    ///     self-provisioning engine fetches its CUDA runtime on first GPU use (see
    ///     <see cref="ProvisionsCudaRuntimeOnDemand" />). The host surfaces this as a
    ///     download progress bar instead of a static spinner. Default delegates to
    ///     <see cref="LoadModelAsync(string, CancellationToken)" /> (no progress), so
    ///     engines with nothing slow to provision need not override it.
    /// </summary>
    // ReSharper disable once UnusedParameter.Global
    Task LoadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
    {
        return LoadModelAsync(modelId, ct);
    }

    /// <summary>Deletes downloaded model files for the given model ID.</summary>
    // ReSharper disable UnusedParameter.Global
    Task DeleteModelAsync(string modelId, CancellationToken ct)
        // ReSharper restore UnusedParameter.Global
    {
        return Task.CompletedTask;
    }

    /// <summary>Opens a real-time streaming session; the host feeds PCM16 audio into it.
    ///     Only called when <see cref="SupportsStreaming" /> is true.</summary>
    // ReSharper disable once UnusedParameter.Global
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
    ///     Transcribes audio with streaming progress updates via <paramref name="onProgress" />,
    ///     which receives partial transcription text and returns <c>false</c> to cancel.
    ///     Default delegates to <see cref="TranscribeAsync" />.
    /// </summary>
    Task<PluginTranscriptionResult> TranscribeStreamingAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        // ReSharper disable once UnusedParameter.Global
        Func<string, bool> onProgress,
        CancellationToken ct
    )
    {
        return TranscribeAsync(wavAudio, language, translate, prompt, ct);
    }
}
