using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>
///     The worker only runs inference. It reads the host's saved plugin settings once and keeps its
///     own writes in memory: the host process owns the settings file, and a second writer could lose
///     updates. Secrets, the event bus and the foreground window stay with the host.
/// </summary>
internal sealed class TranscriptionWorkerHostServices : IPluginHostServices
{
    private const string SecretPrefix = "secret:";
    private const string NoSecretsMessage = "The transcription worker has no secret storage.";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Lock _sync = new();
    private readonly Action<PluginLogLevel, string> _log;
    private Dictionary<string, JsonElement> _settings = [];

    internal TranscriptionWorkerHostServices(
        string packageDirectory,
        string pluginDataRoot,
        string assetDirectory,
        Action<PluginLogLevel, string> log
    )
    {
        _log = log;
        PluginAssetDirectory = assetDirectory;
        Localization = new PluginLocalization(packageDirectory);
        // The data directory is only known once the manifest names the plugin; LoadPlugin
        // resolves it the same way, as <root>/<id>.
        PluginDataRoot = pluginDataRoot;
        Processes = new PluginProcessSupervisorScope("transcription-worker", new ProcessRunner());
    }

    private string PluginDataRoot { get; }
    private string? _pluginDataDirectory;

    public string PluginDataDirectory =>
        _pluginDataDirectory ?? throw new InvalidOperationException("The plugin is not loaded yet.");

    public string PluginAssetDirectory { get; }
    public string? ActiveAppProcessName => null;
    public string? ActiveAppName => null;
    public IPluginEventBus EventBus { get; } = new DetachedEventBus();
    public IReadOnlyList<string> AvailableProfileNames => [];
    public IPluginLocalization Localization { get; }
    public IPluginProcessSupervisor Processes { get; }

    // The host decides when the worker ends; an engine must not release its model on its own.
    public TimeSpan? LocalModelIdleTimeout => null;

    /// <summary>Reads the plugin's saved settings once, after the manifest named the plugin.</summary>
    internal void Bind(string pluginId)
    {
        _pluginDataDirectory = Path.Join(PluginDataRoot, pluginId);
        var settings = ReadSettings(Path.Join(_pluginDataDirectory, "settings.json"));
        lock (_sync)
        {
            _settings = settings;
        }
    }

    public void Log(PluginLogLevel level, string message)
    {
        _log(level, message);
    }

    public void NotifyCapabilitiesChanged()
    {
        // The host's own plugin instance publishes capabilities; the worker has nobody to tell.
    }

    public T? GetSetting<T>(string key)
    {
        if (key.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Secret keys are not settings.", nameof(key));
        }

        JsonElement value;
        lock (_sync)
        {
            if (!_settings.TryGetValue(key, out value))
            {
                return default;
            }
        }

        try
        {
            return value.Deserialize<T>(s_jsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public void SetSetting<T>(string key, T value)
    {
        if (key.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Secret keys are not settings.", nameof(key));
        }

        var element = JsonSerializer.SerializeToElement(value, s_jsonOptions);
        lock (_sync)
        {
            _settings[key] = element;
        }
    }

    public Task StoreSecretAsync(string key, string value)
    {
        throw new NotSupportedException(NoSecretsMessage);
    }

    public Task<string?> LoadSecretAsync(string key)
    {
        throw new NotSupportedException(NoSecretsMessage);
    }

    public Task DeleteSecretAsync(string key)
    {
        throw new NotSupportedException(NoSecretsMessage);
    }

    // Secret entries never enter the worker, even encrypted.
    private static Dictionary<string, JsonElement> ReadSettings(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream, s_jsonOptions) ?? [];
            return settings
                .Where(entry => !entry.Key.StartsWith(SecretPrefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"[TranscriptionWorker] Plugin settings unavailable: {ex.GetType().Name}");
            return [];
        }
    }

    // Plugins may subscribe while they activate. Events stay in the host, so subscriptions are
    // accepted and never called, and publishing goes nowhere.
    private sealed class DetachedEventBus : IPluginEventBus
    {
        private static readonly IDisposable s_emptySubscription = new EmptySubscription();

        public void Publish<T>(T pluginEvent)
            where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent
        {
            return s_emptySubscription;
        }

        private sealed class EmptySubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
