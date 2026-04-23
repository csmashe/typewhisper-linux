using System.ComponentModel;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services;

public sealed class ModelManagerService : INotifyPropertyChanged, IDisposable
{
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, ModelStatus> _modelStatuses = new();
    private string? _activeModelId;
    private System.Timers.Timer? _autoUnloadTimer;
    private bool _disposed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? ActiveModelId
    {
        get => _activeModelId;
        private set { _activeModelId = value; OnPropertyChanged(); }
    }

    public PluginManager PluginManager => _pluginManager;

    public static bool IsPluginModel(string modelId) => modelId.StartsWith("plugin:");

    public static (string PluginId, string ModelId) ParsePluginModelId(string modelId)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Not a plugin model ID: {modelId}");

        var firstColon = modelId.IndexOf(':');
        var secondColon = modelId.IndexOf(':', firstColon + 1);
        if (secondColon < 0)
            throw new ArgumentException($"Invalid plugin model ID format: {modelId}");

        return (modelId[(firstColon + 1)..secondColon], modelId[(secondColon + 1)..]);
    }

    public static string GetPluginModelId(string pluginId, string modelId) =>
        $"plugin:{pluginId}:{modelId}";

    public ITranscriptionEngine Engine
    {
        get
        {
            if (_activeModelId is not null && IsPluginModel(_activeModelId))
            {
                var (pluginId, _) = ParsePluginModelId(_activeModelId);
                var plugin = _pluginManager.TranscriptionEngines
                    .FirstOrDefault(e => e.PluginId == pluginId);
                if (plugin is not null)
                    return new PluginTranscriptionEngineAdapter(plugin);
            }

            return NoOpTranscriptionEngine.Instance;
        }
    }

    public ITranscriptionEnginePlugin? ActiveTranscriptionPlugin
    {
        get
        {
            if (_activeModelId is null || !IsPluginModel(_activeModelId))
                return null;
            var (pluginId, _) = ParsePluginModelId(_activeModelId);
            return _pluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId);
        }
    }

    public ModelManagerService(PluginManager pluginManager, ISettingsService settings)
    {
        _pluginManager = pluginManager;
        _settings = settings;
    }

    public ModelStatus GetStatus(string modelId)
    {
        if (_modelStatuses.TryGetValue(modelId, out var tracked))
            return tracked;

        if (!IsPluginModel(modelId))
            return ModelStatus.NotDownloaded;

        if (_activeModelId == modelId)
            return ModelStatus.Ready;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId);

        if (plugin is null)
            return ModelStatus.NotDownloaded;

        if (plugin.SupportsModelDownload)
            return plugin.IsModelDownloaded(pluginModelId) ? ModelStatus.Ready : ModelStatus.NotDownloaded;

        return plugin.IsConfigured ? ModelStatus.Ready : ModelStatus.NotDownloaded;
    }

    public bool IsDownloaded(string modelId)
    {
        if (!IsPluginModel(modelId))
            return false;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId);

        if (plugin is null)
            return false;

        if (plugin.SupportsModelDownload)
            return plugin.IsModelDownloaded(pluginModelId);

        return plugin.IsConfigured;
    }

    public async Task DownloadAndLoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        try
        {
            if (plugin.SupportsModelDownload && !plugin.IsModelDownloaded(pluginModelId))
            {
                SetStatus(modelId, ModelStatus.DownloadingModel(0));

                var progress = new Progress<double>(p => SetStatus(modelId, ModelStatus.DownloadingModel(p)));
                await plugin.DownloadModelAsync(pluginModelId, progress, cancellationToken);
            }

            await LoadModelAsync(modelId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (!IsPluginModel(modelId))
            throw new ArgumentException($"Unknown model: {modelId}");

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId)
            ?? throw new ArgumentException($"Unknown plugin: {pluginId}");

        if (!plugin.IsConfigured && !plugin.SupportsModelDownload)
            throw new InvalidOperationException(
                $"{plugin.ProviderDisplayName}: not configured (missing API key or model).");

        CancelAutoUnload();
        SetStatus(modelId, ModelStatus.LoadingModel);
        try
        {
            plugin.ConfigureComputeBackend(_settings.Current.ComputeBackend);

            if (plugin.SupportsModelDownload)
                await plugin.LoadModelAsync(pluginModelId, cancellationToken);

            plugin.SelectModel(pluginModelId);
            SetStatus(modelId, ModelStatus.Ready);
            ActiveModelId = modelId;
        }
        catch (Exception ex)
        {
            SetStatus(modelId, ModelStatus.Failed(ex.Message));
            throw;
        }
    }

    public void UnloadModel()
    {
        CancelAutoUnload();
        if (ActiveModelId is not null)
        {
            var plugin = ActiveTranscriptionPlugin;
            plugin?.UnloadModelAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                    System.Diagnostics.Debug.WriteLine($"UnloadModelAsync failed: {t.Exception?.Message}");
            });
            SetStatus(ActiveModelId, ModelStatus.NotDownloaded);
            ActiveModelId = null;
        }
    }

    public void ScheduleAutoUnload()
    {
        CancelAutoUnload();

        var seconds = _settings.Current.ModelAutoUnloadSeconds;
        if (seconds <= 0 || ActiveModelId is null)
            return;

        _autoUnloadTimer = new System.Timers.Timer(seconds * 1000.0)
        {
            AutoReset = false,
        };
        _autoUnloadTimer.Elapsed += (_, _) =>
        {
            System.Diagnostics.Debug.WriteLine($"Auto-unloading model after {seconds}s idle");
            UnloadModel();
        };
        _autoUnloadTimer.Start();
    }

    private void CancelAutoUnload()
    {
        _autoUnloadTimer?.Stop();
        _autoUnloadTimer?.Dispose();
        _autoUnloadTimer = null;
    }

    public bool CanDeleteModel(string modelId)
    {
        if (!IsPluginModel(modelId))
            return false;

        var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
        var plugin = _pluginManager.TranscriptionEngines
            .FirstOrDefault(e => e.PluginId == pluginId);

        return plugin is { SupportsModelDownload: true } && plugin.IsModelDownloaded(pluginModelId);
    }

    public async Task DeleteModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (ActiveModelId == modelId)
        {
            var plugin = ActiveTranscriptionPlugin;
            if (plugin is not null)
                await plugin.UnloadModelAsync();

            ActiveModelId = null;
        }

        if (IsPluginModel(modelId))
        {
            var (pluginId, pluginModelId) = ParsePluginModelId(modelId);
            var plugin = _pluginManager.TranscriptionEngines
                .FirstOrDefault(e => e.PluginId == pluginId);

            if (plugin is { SupportsModelDownload: true })
                await plugin.DeleteModelAsync(pluginModelId, cancellationToken);
        }

        SetStatus(modelId, ModelStatus.NotDownloaded);
    }

    public void DeleteModel(string modelId) =>
        _ = DeleteModelAsync(modelId);

    public async Task<bool> EnsureModelLoadedAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var targetModelId = modelId ?? _settings.Current.SelectedModelId;
        if (string.IsNullOrWhiteSpace(targetModelId))
            return false;

        if (ActiveModelId == targetModelId)
        {
            CancelAutoUnload();
            return true;
        }

        if (!IsDownloaded(targetModelId))
            await DownloadAndLoadModelAsync(targetModelId, cancellationToken);
        else
            await LoadModelAsync(targetModelId, cancellationToken);

        return true;
    }

    public void MigrateSettings()
    {
        var current = _settings.Current;
        var changed = false;

        var migratedModelId = MigrateModelId(current.SelectedModelId);
        if (migratedModelId != current.SelectedModelId)
        {
            current = current with { SelectedModelId = migratedModelId };
            changed = true;
        }

        if (changed)
            _settings.Save(current);
    }

    public static string? MigrateModelId(string? modelId) => modelId switch
    {
        "parakeet-tdt-0.6b" => GetPluginModelId("com.typewhisper.sherpa-onnx", "parakeet-tdt-0.6b"),
        "canary-1b-flash" or "canary-180m-flash" => GetPluginModelId("com.typewhisper.sherpa-onnx", "canary-180m-flash"),
        _ => modelId
    };

    private void SetStatus(string modelId, ModelStatus status)
    {
        _modelStatuses[modelId] = status;
        OnPropertyChanged(nameof(GetStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (!_disposed)
        {
            CancelAutoUnload();
            _disposed = true;
        }
    }
}

internal sealed class NoOpTranscriptionEngine : ITranscriptionEngine
{
    public static readonly NoOpTranscriptionEngine Instance = new();

    public bool IsModelLoaded => false;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void UnloadModel() { }

    public Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptionResult { Text = string.Empty });
}

internal sealed class PluginTranscriptionEngineAdapter : ITranscriptionEngine
{
    private readonly ITranscriptionEnginePlugin _plugin;

    public PluginTranscriptionEngineAdapter(ITranscriptionEnginePlugin plugin) => _plugin = plugin;

    public bool IsModelLoaded => _plugin.IsConfigured && _plugin.SelectedModelId is not null;

    public Task LoadModelAsync(string modelPath, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void UnloadModel() { }

    public async Task<TranscriptionResult> TranscribeAsync(
        float[] audioSamples, string? language = null,
        TranscriptionTask task = TranscriptionTask.Transcribe,
        CancellationToken cancellationToken = default)
    {
        var wavBytes = WavEncoder.Encode(audioSamples);
        var translate = task == TranscriptionTask.Translate;
        var result = await _plugin.TranscribeAsync(wavBytes, language, translate, null, cancellationToken);
        return new TranscriptionResult
        {
            Text = result.Text,
            DetectedLanguage = result.DetectedLanguage,
            Duration = result.DurationSeconds,
            NoSpeechProbability = result.NoSpeechProbability
        };
    }
}
