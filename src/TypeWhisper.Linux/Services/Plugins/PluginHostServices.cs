using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Per-plugin host services for the Linux shell. Each plugin gets its own
///     instance with isolated settings storage and secret management scoped to
///     its plugin ID.
/// </summary>
public sealed class PluginHostServices : IPluginHostServices
{
    private const string SecretPrefix = "secret:";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true
    };

    private readonly IActiveWindowService _activeWindow;
    private readonly PluginLocalization _localization;
    private readonly Action? _onCapabilitiesChanged;
    private readonly string _pluginDataDirectory;
    private readonly string _pluginDataRoot;
    private readonly ISettingsService? _settings;
    private readonly IErrorLogService? _errorLog;
    private readonly string _pluginErrorCategory;
    private readonly string _pluginDisplayName;

    private readonly string _pluginId;
    private readonly IProfileService _profiles;
    private readonly string _settingsFilePath;
    private readonly Lock _settingsLock = new();

    private Dictionary<string, JsonElement>? _settingsCache;

    public PluginHostServices(
        string pluginId,
        string pluginDirectory,
        IActiveWindowService activeWindow,
        IPluginEventBus eventBus,
        IProfileService profiles,
        ISettingsService? settings = null,
        Action? onCapabilitiesChanged = null,
        IErrorLogService? errorLog = null,
        string? errorCategory = null,
        string? pluginDisplayName = null,
        string? pluginDataRoot = null
    )
    {
        _pluginId = pluginId;
        _activeWindow = activeWindow;
        EventBus = eventBus;
        _profiles = profiles;
        _settings = settings;
        _onCapabilitiesChanged = onCapabilitiesChanged;
        _errorLog = errorLog;
        // Already resolved by the host (PluginManager) from manifest + capabilities.
        _pluginErrorCategory = string.IsNullOrWhiteSpace(errorCategory) ? ErrorCategory.Plugin : errorCategory;
        _pluginDisplayName = string.IsNullOrWhiteSpace(pluginDisplayName) ? pluginId : pluginDisplayName;
        _localization = new PluginLocalization(pluginDirectory);
        _pluginDataRoot = pluginDataRoot ?? TypeWhisperEnvironment.PluginDataPath;
        _pluginDataDirectory = Path.Join(_pluginDataRoot, pluginId);
        _settingsFilePath = Path.Join(_pluginDataDirectory, "settings.json");
    }

    public string PluginDataDirectory
    {
        get
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            return _pluginDataDirectory;
        }
    }

    // Large model/runtime assets follow the user-configured storage path when set;
    // falls back to PluginDataDirectory (under AppData) when no custom path is configured
    // or no settings service was provided.
    public string PluginAssetDirectory
    {
        get
        {
            return _settings is null
                ? PluginDataDirectory
                : LocalModelStorageService.ResolveAvailablePluginAssetDirectory(
                    _settings.Current,
                    _pluginId,
                    _pluginDataRoot
                );
        }
    }

    public string? ActiveAppProcessName => _activeWindow.GetActiveWindowProcessName();
    public string? ActiveAppName => _activeWindow.GetActiveWindowTitle();

    public IPluginEventBus EventBus { get; }

    public IPluginLocalization Localization => _localization;

    public IReadOnlyList<string> AvailableProfileNames =>
        _profiles.Profiles.Select(p => p.Name).ToList();

    public void Log(PluginLogLevel level, string message)
    {
        Trace.WriteLine($"[Plugin:{_pluginId}] [{level}] {message}");

        // Bridge plugin-reported errors onto the user-facing error log so failures that
        // would otherwise vanish into Trace (invalid key, model load, provider outage)
        // show up in About and bug-report diagnostics. Lower levels stay diagnostic-only
        // to keep the 200-entry buffer signal-rich.
        if (level != PluginLogLevel.Error)
        {
            return;
        }

        try
        {
            _errorLog?.AddEntry($"{_pluginDisplayName}: {message}", _pluginErrorCategory);
        }
        catch
        {
            // Diagnostics must never destabilize a plugin's own logging call.
        }
    }

    public void NotifyCapabilitiesChanged()
    {
        Trace.WriteLine($"[Plugin:{_pluginId}] Capabilities changed, notifying host");
        _onCapabilitiesChanged?.Invoke();
    }

    public Task StoreSecretAsync(string key, string value)
    {
        var encrypted = ApiKeyProtection.Encrypt(value);
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            settings[$"{SecretPrefix}{key}"] = JsonSerializer.SerializeToElement(encrypted);
            SaveSettings(settings);
        }

        return Task.CompletedTask;
    }

    public Task<string?> LoadSecretAsync(string key)
    {
        string? encrypted;
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            encrypted = settings.TryGetValue($"{SecretPrefix}{key}", out var element)
                ? element.Deserialize<string>()
                : null;
        }

        return Task.FromResult(encrypted is null ? null : ApiKeyProtection.Decrypt(encrypted));
    }

    public Task DeleteSecretAsync(string key)
    {
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            settings.Remove($"{SecretPrefix}{key}");
            SaveSettings(settings);
        }

        return Task.CompletedTask;
    }

    public T? GetSetting<T>(string key)
    {
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            if (!settings.TryGetValue(key, out var element))
            {
                return default;
            }

            try
            {
                return element.Deserialize<T>(s_jsonOptions);
            }
            catch (JsonException ex)
            {
                Trace.WriteLine(
                    $"[Plugin:{_pluginId}] Failed to deserialize setting '{key}': {ex.Message}"
                );
                return default;
            }
        }
    }

    public void SetSetting<T>(string key, T value)
    {
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);
            SaveSettings(settings);
        }
    }

    private Dictionary<string, JsonElement> LoadSettings()
    {
        // C# Monitor is re-entrant for the same thread, so callers already holding
        // _settingsLock (GetSetting, SetSetting, etc.) can call LoadSettings safely.
        lock (_settingsLock)
        {
            if (_settingsCache is not null)
            {
                return _settingsCache;
            }

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    _settingsCache =
                        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            json,
                            s_jsonOptions
                        ) ?? [];
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Plugin:{_pluginId}] Failed to load settings: {ex.Message}");
                    _settingsCache = [];
                }
            }
            else
            {
                _settingsCache = [];
            }

            return _settingsCache;
        }
    }

    private void SaveSettings(Dictionary<string, JsonElement> settings)
    {
        try
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            var json = JsonSerializer.Serialize(settings, s_jsonOptions);
            // Non-atomic write — acceptable for small, infrequently written plugin settings;
            // a future pass could adopt temp-file + rename like the main config.
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Plugin:{_pluginId}] Failed to save settings: {ex.Message}");
        }
    }
}
