using System.Collections.Immutable;
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
        WriteIndented = true, PropertyNameCaseInsensitive = true,
    };
    private static readonly PluginStateStoreOptions s_defaultStateStoreOptions = new();

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
    private readonly AtomicJsonStore<ImmutableDictionary<string, JsonElement>> _settingsStore;
    private readonly Lock _stateStoresLock = new();
    private readonly Dictionary<string, StateStoreRegistration> _stateStores =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // Backup files a store will generate. Tracked alongside primaries so one store's backup
    // cannot land on another store's primary.
    private readonly HashSet<string> _reservedBackupPaths =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        var settingsFilePath = Path.Join(_pluginDataDirectory, "settings.json");
        _secretProtectionKeyFilePath = ResolveSecretProtectionKeyFilePath(
            _pluginDataRoot,
            secretProtectionKeyFilePath
        );
        _settingsStore = new AtomicJsonStore<ImmutableDictionary<string, JsonElement>>(
            settingsFilePath,
            static () => ImmutableDictionary<string, JsonElement>.Empty,
            new AtomicJsonStoreOptions<ImmutableDictionary<string, JsonElement>>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = DeserializeSettings,
                Diagnostic = ReportSettingsDiagnostic,
            }
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
        SaveSettings(
            current =>
                current.SetItem(
                    $"{SecretPrefix}{key}",
                    JsonSerializer.SerializeToElement(encrypted)
                )
        );
        return Task.CompletedTask;
    }

    public Task<string?> LoadSecretAsync(string key)
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
            SaveSettings(
                current =>
                {
                    var next = current;
                    foreach (var property in current)
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
                            throw new InvalidDataException(
                                $"'{property.Key}' could not be authenticated"
                            );
                        }

                        if (result.RequiresMigration)
                        {
                            next = next.SetItem(
                                property.Key,
                                JsonSerializer.SerializeToElement(
                                    ApiKeyProtection.Encrypt(
                                        result.PlainText,
                                        _secretProtectionKeyFilePath
                                    )
                                )
                            );
                        }
                    }

                    return next;
                }
            );
            return Task.FromResult(requested.PlainText);
        }
        catch (Exception ex)
        {
            LogSecretUnavailable(key, $"migration failed: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }

    public Task DeleteSecretAsync(string key)
    {
        SaveSettings(current => current.Remove($"{SecretPrefix}{key}"));
        return Task.CompletedTask;
    }

    public T? GetSetting<T>(string key)
    {
        ThrowIfReservedSecretKey(key);
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

    public void SetSetting<T>(string key, T value)
    {
        ThrowIfReservedSecretKey(key);
        SaveSettings(
            current =>
                current.SetItem(key, JsonSerializer.SerializeToElement(value, s_jsonOptions))
        );
    }

    public IPluginStateStore<T> OpenStateStore<T>(
        string fileName,
        Func<T> createDefault,
        PluginStateStoreOptions? options = null
    )
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(createDefault);
        if (
            Path.IsPathRooted(fileName)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "Plugin state file names must be leaf file names.",
                nameof(fileName)
            );
        }

        if (
            string.Equals(fileName, "settings.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                fileName,
                "secret-protection.key",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new ArgumentException(
                $"'{fileName}' is reserved for host-managed plugin state.",
                nameof(fileName)
            );
        }

        var path = Path.GetFullPath(Path.Join(_pluginDataDirectory, fileName));
        var normalizedRoot = Path.GetFullPath(_pluginDataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(path)
            ?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(parent, normalizedRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Plugin state file names must be leaf file names.",
                nameof(fileName)
            );
        }

        var normalizedOptions = options ?? s_defaultStateStoreOptions;
        lock (_stateStoresLock)
        {
            if (_stateStores.TryGetValue(path, out var existing))
            {
                if (
                    existing.StateType != typeof(T)
                    || !OptionsMatch(existing.Options, normalizedOptions)
                )
                {
                    throw new InvalidOperationException(
                        $"Plugin state path '{path}' was already opened with a conflicting "
                        + "state type or options."
                    );
                }

                return (IPluginStateStore<T>)existing.Store;
            }

            var backupPath = normalizedOptions.KeepLastKnownGoodBackup ? path + ".bak" : null;
            if (_reservedBackupPaths.Contains(path))
            {
                throw new InvalidOperationException(
                    $"Plugin state path '{path}' is already reserved as the backup file of "
                    + "another state store."
                );
            }

            if (backupPath is not null && _stateStores.ContainsKey(backupPath))
            {
                throw new InvalidOperationException(
                    $"Plugin state path '{path}' would back up onto '{backupPath}', which is "
                    + "already open as a state store."
                );
            }

            var coreOptions = new AtomicJsonStoreOptions<T>
            {
                JsonOptions = normalizedOptions.JsonOptions,
                BackupMode = normalizedOptions.KeepLastKnownGoodBackup
                    ? AtomicJsonBackupMode.LastKnownGood
                    : AtomicJsonBackupMode.None,
                CorruptFilePolicy =
                    normalizedOptions.CorruptFilePolicy
                    == PluginStateCorruptFilePolicy.PreserveAndReset
                        ? AtomicJsonCorruptFilePolicy.PreserveAndReset
                        : AtomicJsonCorruptFilePolicy.Throw,
                Diagnostic = ReportStateStoreDiagnostic,
            };
            var adapter = new PluginStateStoreAdapter<T>(
                new AtomicJsonStore<T>(path, createDefault, coreOptions)
            );
            _stateStores.Add(
                path,
                new StateStoreRegistration(typeof(T), normalizedOptions, adapter)
            );
            if (backupPath is not null)
            {
                _reservedBackupPaths.Add(backupPath);
            }

            return adapter;
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

    private ImmutableDictionary<string, JsonElement> LoadSettings()
    {
        return _settingsStore.Current;
    }

    private void SaveSettings(
        Func<
            ImmutableDictionary<string, JsonElement>,
            ImmutableDictionary<string, JsonElement>
        > update
    )
    {
        try
        {
            _settingsStore.Update(update);
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

    private static ImmutableDictionary<string, JsonElement> DeserializeSettings(
        string json
    )
    {
        return JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(
                json,
                s_jsonOptions
            )
            ?? throw new JsonException("The settings file contained null JSON.");
    }

    private void ReportStateStoreDiagnostic(AtomicJsonStoreDiagnostic diagnostic)
    {
        Trace.WriteLine(
            $"[Plugin:{_pluginId}] State store {diagnostic.Kind} at '{diagnostic.Path}'."
        );
        if (diagnostic.Kind != AtomicJsonStoreDiagnosticKind.CorruptFilePreserved)
        {
            return;
        }

        AddSettingsError(
            $"Plugin '{_pluginDisplayName}' ({_pluginId}) state file '{diagnostic.Path}' was "
            + $"corrupt and was preserved as '{diagnostic.PreservedPath}': "
            + (diagnostic.Exception?.Message ?? "invalid JSON")
        );
    }

    private void ReportSettingsDiagnostic(AtomicJsonStoreDiagnostic diagnostic)
    {
        if (diagnostic.Kind != AtomicJsonStoreDiagnosticKind.CorruptFilePreserved)
        {
            return;
        }

        AddSettingsError(
            $"Plugin '{_pluginDisplayName}' ({_pluginId}) settings were corrupt and "
            + $"were preserved as '{diagnostic.PreservedPath}': "
            + (diagnostic.Exception?.Message ?? "invalid JSON")
        );
    }

    private static bool OptionsMatch(
        PluginStateStoreOptions left,
        PluginStateStoreOptions right
    )
    {
        return ReferenceEquals(left.JsonOptions, right.JsonOptions)
            && left.KeepLastKnownGoodBackup == right.KeepLastKnownGoodBackup
            && left.CorruptFilePolicy == right.CorruptFilePolicy;
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

    private sealed record StateStoreRegistration(
        Type StateType,
        PluginStateStoreOptions Options,
        object Store
    );

    private sealed class PluginStateStoreAdapter<T>(AtomicJsonStore<T> store)
        : IPluginStateStore<T>
        where T : notnull
    {
        public ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(store.Current);
        }

        public ValueTask<T> UpdateAsync(
            Func<T, T> update,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(update);
            cancellationToken.ThrowIfCancellationRequested();

            // Rechecked inside the transaction: this call may have waited for another one to
            // finish, and cancellation during that wait must not still commit.
            return ValueTask.FromResult(
                store.Update(current =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return update(current);
                })
            );
        }
    }
}
