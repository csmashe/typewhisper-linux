using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="ISettingsService" />: loads settings (falling back to a backup copy
///     and then defaults), applies legacy-field migrations, and persists changes atomically.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _commitGate = new();
    private readonly AtomicJsonStore<AppSettings> _store;

    public SettingsService(string filePath)
    {
        _store = new AtomicJsonStore<AppSettings>(
            filePath,
            () => AppSettings.Default,
            new AtomicJsonStoreOptions<AppSettings>
            {
                JsonOptions = s_jsonOptions,
                BackupMode = AtomicJsonBackupMode.LastKnownGood,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = Deserialize,
                Diagnostic = diagnostic =>
                    LogWarning(
                        $"{diagnostic.Kind} at {diagnostic.Path}"
                        + (
                            diagnostic.Exception is null
                                ? string.Empty
                                : $": {diagnostic.Exception.Message}"
                        )
                    ),
            }
        );
        _ = _store.Current;
    }

    public AppSettings Current => _store.Current;

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Load()
    {
        return _store.Reload();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Commit(_ => settings);
    }

    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return Commit(mutate);
    }

    private AppSettings Commit(Func<AppSettings, AppSettings> update)
    {
        // `changed` is decided by the store from the persisted form, not by comparing
        // AppSettings by reference here, which would miss real changes and announce false ones.
        // The store commits atomically but releases before returning, so without this lock
        // two concurrent saves could notify out of order and leave a subscriber applying
        // superseded settings.
        lock (_commitGate)
        {
            var committed = _store.Update(update, out var changed);
            if (changed)
            {
                SettingsChanged?.Invoke(committed);
            }

            return committed;
        }
    }

    private static AppSettings Deserialize(string json)
    {
        var settings =
            JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions)
            ?? throw new JsonException("Settings JSON deserialized to null.");
        settings = ApplyHistoryRetentionMigration(settings, json);
        return ApplyAccelerationMigration(settings, json);
    }

    /// <summary>
    ///     Migrates the legacy <c>historyRetentionDays</c> int field to the current
    ///     <c>historyRetentionMode</c> / <c>historyRetentionMinutes</c> pair.
    ///     9999 was the sentinel for "keep forever".
    /// </summary>
    private static AppSettings ApplyHistoryRetentionMigration(AppSettings settings, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return settings;
        }

        if (root is not JsonObject obj)
        {
            return settings;
        }

        if (
            !obj.ContainsKey("historyRetentionMode")
            && obj.TryGetPropertyValue("historyRetentionDays", out var legacyNode)
        )
        {
            var legacyDays = legacyNode?.GetValue<int?>();
            return legacyDays switch
            {
                9999 => settings with { HistoryRetentionMode = HistoryRetentionMode.Forever },
                > 0 => settings with
                {
                    HistoryRetentionMode = HistoryRetentionMode.Duration,
                    // Widen to long to avoid int overflow on pathological legacy values (>~1.5M days).
                    HistoryRetentionMinutes = (int)Math.Min(
                        (long)legacyDays.Value * 24 * 60,
                        int.MaxValue
                    ),
                },
                _ => settings with
                {
                    HistoryRetentionMode = AppSettings.Default.HistoryRetentionMode,
                    HistoryRetentionMinutes = AppSettings.Default.HistoryRetentionMinutes,
                },
            };
        }

        if (
            settings is { HistoryRetentionMode: HistoryRetentionMode.Duration, HistoryRetentionMinutes: <= 0 }
        )
        {
            return settings with { HistoryRetentionMinutes = AppSettings.Default.HistoryRetentionMinutes };
        }

        return settings;
    }

    /// <summary>
    ///     Migrates the legacy <c>computeBackend</c> ("cpu"/"cuda") to
    ///     <c>localModelAcceleration</c> (Auto/Cpu/NvidiaCuda). Only runs when the
    ///     legacy field exists and the new field does not — idempotent on every load.
    /// </summary>
    private static AppSettings ApplyAccelerationMigration(AppSettings settings, string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return settings with
            {
                LocalModelAcceleration = AppSettings.NormalizeLocalModelAcceleration(
                    settings.LocalModelAcceleration
                ),
            };
        }

        if (root is not JsonObject obj
            || obj.ContainsKey("localModelAcceleration")
            || !obj.TryGetPropertyValue("computeBackend", out var legacyNode))
        {
            return settings with
            {
                LocalModelAcceleration = AppSettings.NormalizeLocalModelAcceleration(
                    settings.LocalModelAcceleration
                ),
            };
        }

        var legacy = legacyNode?.GetValue<string?>();
        var migrated = string.Equals(legacy, "cuda", StringComparison.OrdinalIgnoreCase)
            ? AppSettings.LocalModelAccelerationNvidiaCuda
            : AppSettings.LocalModelAccelerationCpu;

        return settings with { LocalModelAcceleration = migrated };

    }

    private static void LogWarning(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SettingsService] {message}";
        Debug.WriteLine(line);

        try
        {
            var logDir = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeWhisper",
                "Logs"
            );
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Join(logDir, "settings.log"), line + Environment.NewLine);
        }
        catch
        {
            /* logging must never throw */
        }
    }
}
