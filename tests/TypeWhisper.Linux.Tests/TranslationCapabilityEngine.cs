using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Engine whose models declare translation per model: "multilingual" can, "english-only"
///     cannot, and "legacy" declares nothing (engine-wide flag only). Records every load,
///     selection and transcription so tests can prove a refusal happened before any of them.
/// </summary>
internal sealed class TranslationCapabilityEngine(string selectedModelId) : ITranscriptionEngineRole
{
    private const string Id = "test-translation";

    public string PluginId => Id;
    public string ProviderId => Id;
    public string ProviderDisplayName => "Test translation";
    public bool IsConfigured => true;
    public bool SupportsModelDownload => true;

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [
        new("multilingual", "Multilingual") { SupportsTranslation = true },
        new("english-only", "English Only") { SupportsTranslation = false },
        new("legacy", "Legacy"),
    ];

    public string? SelectedModelId { get; private set; } = selectedModelId;

    // Engine-wide flag a legacy (undeclared) model falls back to.
    public bool SupportsTranslation =>
        TranscriptionModels.FirstOrDefault(model => model.Id == SelectedModelId)?.SupportsTranslation ?? false;

    public int LoadCount { get; private set; }
    public int SelectCount { get; private set; }
    public int TranscribeCount { get; private set; }
    public bool? LastTranslate { get; private set; }

    public void SelectModel(string modelId)
    {
        SelectCount++;
        SelectedModelId = modelId;
    }

    public bool IsModelDownloaded(string modelId) => true;

    public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct) =>
        Task.CompletedTask;

    public Task LoadModelAsync(string modelId, CancellationToken ct)
    {
        LoadCount++;
        SelectedModelId = modelId;
        return Task.CompletedTask;
    }

    public Task UnloadModelAsync() => Task.CompletedTask;

    public Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
    {
        TranscribeCount++;
        LastTranslate = translate;
        return Task.FromResult(new PluginTranscriptionResult("transcribed", "en", 1));
    }

    public static string FullModelId(string modelId) => ModelManagerService.GetPluginModelId(Id, modelId);
}
