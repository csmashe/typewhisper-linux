using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        WriteIndented = true, PropertyNameCaseInsensitive = true,
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
    private readonly string _secretProtectionKeyFilePath;
    private readonly string _settingsFilePath;
    private readonly Lock _settingsLock = new();

    private bool _loadFailed;
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
        string? pluginDataRoot = null,
        string? secretProtectionKeyFilePath = null
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
        _secretProtectionKeyFilePath = ResolveSecretProtectionKeyFilePath(
            _pluginDataRoot,
            secretProtectionKeyFilePath
        );
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
            // _errorLog is readonly; _settingsLock guards _settingsCache/_loadFailed, not this,
            // so reading it unlocked is race-free.
            // ReSharper disable once InconsistentlySynchronizedField
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
        var encrypted = ApiKeyProtection.Encrypt(value, _secretProtectionKeyFilePath);
        lock (_settingsLock)
        {
            var current = LoadSettings();
            var next = new Dictionary<string, JsonElement>(current)
            {
                [$"{SecretPrefix}{key}"] = JsonSerializer.SerializeToElement(encrypted),
            };
            SaveSettings(next);
            _settingsCache = next;
        }

        return Task.CompletedTask;
    }

    public Task<string?> LoadSecretAsync(string key)
    {
        lock (_settingsLock)
        {
            var settings = LoadSettings();
            if (!settings.TryGetValue($"{SecretPrefix}{key}", out var element))
            {
                return Task.FromResult<string?>(null);
            }

            string? encrypted;
            try
            {
                encrypted = element.Deserialize<string>();
            }
            catch (JsonException ex)
            {
                LogSecretUnavailable(key, ex.Message);
                return Task.FromResult<string?>(null);
            }

            if (encrypted is null)
            {
                return Task.FromResult<string?>(null);
            }

            var requested = ApiKeyProtection.Decrypt(
                encrypted,
                _secretProtectionKeyFilePath
            );
            if (!requested.Succeeded)
            {
                LogSecretUnavailable(key, "the protected value could not be authenticated");
                return Task.FromResult<string?>(null);
            }

            if (!requested.RequiresMigration)
            {
                return Task.FromResult(requested.PlainText);
            }

            try
            {
                var next = new Dictionary<string, JsonElement>(settings);
                foreach (var property in settings)
                {
                    if (!property.Key.StartsWith(SecretPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var stored = property.Value.Deserialize<string>();
                    if (stored is null)
                    {
                        continue;
                    }

                    var result = ApiKeyProtection.Decrypt(
                        stored,
                        _secretProtectionKeyFilePath
                    );
                    if (!result.Succeeded || result.PlainText is null)
                    {
                        LogSecretUnavailable(
                            key,
                            $"'{property.Key}' could not be authenticated"
                        );
                        return Task.FromResult<string?>(null);
                    }

                    if (result.RequiresMigration)
                    {
                        next[property.Key] = JsonSerializer.SerializeToElement(
                            ApiKeyProtection.Encrypt(
                                result.PlainText,
                                _secretProtectionKeyFilePath
                            )
                        );
                    }
                }

                SaveSettings(next);
                _settingsCache = next;
                return Task.FromResult(requested.PlainText);
            }
            catch (Exception ex)
            {
                LogSecretUnavailable(key, $"migration failed: {ex.Message}");
                return Task.FromResult<string?>(null);
            }
        }
    }

    public Task DeleteSecretAsync(string key)
    {
        lock (_settingsLock)
        {
            var current = LoadSettings();
            var next = new Dictionary<string, JsonElement>(current);
            if (!next.Remove($"{SecretPrefix}{key}"))
            {
                if (_loadFailed)
                {
                    // The cache is empty only because the file could not be read; we cannot
                    // conclude the secret is gone, so refuse rather than report a false success.
                    ThrowRefusingToSave();
                }

                return Task.CompletedTask;
            }

            SaveSettings(next);
            _settingsCache = next;
        }

        return Task.CompletedTask;
    }

    public T? GetSetting<T>(string key)
    {
        ThrowIfReservedSecretKey(key);
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
        ThrowIfReservedSecretKey(key);
        lock (_settingsLock)
        {
            var current = LoadSettings();
            var next = new Dictionary<string, JsonElement>(current)
            {
                [key] = JsonSerializer.SerializeToElement(value, s_jsonOptions),
            };
            SaveSettings(next);
            _settingsCache = next;
        }
    }

    // The generic accessors share the settings dictionary with the secret store, so they must
    // not read raw ciphertext back out or write plaintext into it behind the encryption.
    private static void ThrowIfReservedSecretKey(string key)
    {
        if (key.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The 'secret:' key namespace is reserved for StoreSecretAsync/LoadSecretAsync.",
                nameof(key)
            );
        }
    }

    private Dictionary<string, JsonElement> LoadSettings()
    {
        // System.Threading.Lock is re-entrant for the same thread, so callers already holding
        // _settingsLock (GetSetting, SetSetting, etc.) can call LoadSettings safely.
        lock (_settingsLock)
        {
            if (_settingsCache is not null)
            {
                return _settingsCache;
            }

            string json;
            try
            {
                json = File.ReadAllText(_settingsFilePath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A genuinely absent file is an empty store, not a load failure.
                _settingsCache = [];
                return _settingsCache;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file may exist but is unreadable (permissions, locked, transient I/O).
                // Do not treat it as empty, or the next save would wipe the existing data.
                Trace.WriteLine($"[Plugin:{_pluginId}] Failed to read settings: {ex.Message}");
                AddSettingsError(
                    $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings could not be read; saves are disabled to protect the existing file: {ex.Message}"
                );
                _loadFailed = true;
                _settingsCache = [];
                return _settingsCache;
            }

            try
            {
                _settingsCache =
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        json,
                        s_jsonOptions
                    ) ?? throw new JsonException("The settings file contained null JSON.");
            }
            catch (JsonException ex)
            {
                Trace.WriteLine($"[Plugin:{_pluginId}] Failed to parse settings: {ex.Message}");
                var brokenPath = PreserveBrokenFile(_settingsFilePath);
                if (brokenPath is null && File.Exists(_settingsFilePath))
                {
                    // The corrupt original is still on disk; overwriting it would lose the only
                    // copy, so disable saves until it is dealt with.
                    _loadFailed = true;
                }

                AddSettingsError(
                    brokenPath is null
                        ? $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings were corrupt, but the original file could not be preserved: {ex.Message}"
                        : $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings were corrupt and were preserved as '{brokenPath}': {ex.Message}"
                );
                _settingsCache = [];
            }

            return _settingsCache;
        }
    }

    private void SaveSettings(Dictionary<string, JsonElement> settings)
    {
        if (_loadFailed)
        {
            ThrowRefusingToSave();
        }

        try
        {
            Directory.CreateDirectory(_pluginDataDirectory);
            var json = JsonSerializer.Serialize(settings, s_jsonOptions);
            AtomicFileWrite.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Plugin:{_pluginId}] Failed to save settings: {ex.Message}");
            AddSettingsError(
                $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings could not be saved: {ex.Message}"
            );
            throw;
        }
    }

    private void LogSecretUnavailable(string key, string reason)
    {
        var message =
            $"Plugin '{_pluginDisplayName}' ({_pluginId}) secret '{key}' is unavailable: {reason}.";
        Trace.WriteLine($"[Plugin:{_pluginId}] {message}");
        AddSettingsError(message);
    }

    private static string ResolveSecretProtectionKeyFilePath(
        string pluginDataRoot,
        string? secretProtectionKeyFilePath
    )
    {
        if (!string.IsNullOrWhiteSpace(secretProtectionKeyFilePath))
        {
            return Path.GetFullPath(secretProtectionKeyFilePath);
        }

        var fullPluginDataRoot = Path.GetFullPath(pluginDataRoot);
        var basePath = Directory.GetParent(
            Path.TrimEndingDirectorySeparator(fullPluginDataRoot)
        )?.FullName;
        return Path.Join(
            basePath ?? TypeWhisperEnvironment.BasePath,
            "secret-protection.key"
        );
    }

    [DoesNotReturn]
    private void ThrowRefusingToSave()
    {
        Trace.WriteLine(
            $"[Plugin:{_pluginId}] Skipping save to '{_settingsFilePath}': previous load failed and overwriting would discard existing data."
        );
        AddSettingsError(
            $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings were not saved because the existing file could not be read."
        );
        throw new IOException(
            $"Refusing to overwrite '{_settingsFilePath}' because the previous load failed."
        );
    }

    private void AddSettingsError(string message)
    {
        try
        {
            _errorLog?.AddEntry(message, _pluginErrorCategory);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[Plugin:{_pluginId}] Failed to add plugin settings error to the error log: {ex.Message}"
            );
        }
    }

    private static string? PreserveBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var brokenPath =
                $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(path, brokenPath);
            Trace.WriteLine($"[PluginHostServices] Preserved unreadable file as {brokenPath}");
            return brokenPath;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PluginHostServices] Could not preserve unreadable file: {ex.Message}"
            );
            return null;
        }
    }
}
