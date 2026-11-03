using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.UiTests;

/// <summary>A downloadable engine so the Dictation section renders its local model cards.</summary>
internal sealed class UiLocalModelEngine : ITranscriptionEngineRole
{
    public string PluginId => "com.typewhisper.ui-local";
    public string ProviderId => "ui-local";
    public string ProviderDisplayName => "Local engine";
    public bool IsConfigured => true;
    public bool SupportsModelDownload => true;
    public bool SupportsTranslation => false;
    public string? SelectedModelId => null;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new("downloaded", "Downloaded model with a long name that wraps") { SizeDescription = "~1.6 GB" },
        new("recommended", "Recommended model") { SizeDescription = "~879 MB", IsRecommended = true },
        new("available", "Available model"),
    ];

    public bool IsModelDownloaded(string modelId) => modelId == "downloaded";

    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct) =>
        Task.CompletedTask;

    public Task LoadModelAsync(string modelId, CancellationToken ct) => Task.CompletedTask;

    public void SelectModel(string modelId) { }

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
        throw new NotSupportedException();
}
