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

    private readonly Lock _gate = new();
    private readonly string _filePath;

    public SettingsService(string filePath)
    {
        _filePath = filePath;
        Current = Load();
    }

    private string BackupPath => _filePath + ".bak";
    private string TempPath => _filePath + ".tmp";

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Load()
    {
        // Load writes Current, so an unsynchronized Load could clobber what an
        // in-flight Update just persisted.
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private AppSettings LoadLocked()
    {
        var result = TryLoadFrom(_filePath);
        if (result is not null)
        {
            Current = result;
            return Current;
        }

        // Primary failed — try backup, then restore it as primary.
        if (File.Exists(BackupPath))
        {
            LogWarning("Primary settings corrupt or missing, trying backup.");
            result = TryLoadFrom(BackupPath);
            if (result is not null)
            {
                Current = result;
                try { File.Copy(BackupPath, _filePath, true); } catch { /* best effort */ }
                return Current;
            }
        }

        LogWarning("No valid settings found, using defaults.");
        Current = AppSettings.Default;
        return Current;
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            SaveLocked(settings);
        }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var updated = mutate(Current);
            SaveLocked(updated);
            return updated;
        }
    }

    private void SaveLocked(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_filePath))
        {
            try { File.Copy(_filePath, BackupPath, true); } catch { /* best effort */ }
        }

        // Atomic write via .tmp so a crash mid-write can't corrupt the primary file.
        var json = JsonSerializer.Serialize(settings, s_jsonOptions);
        File.WriteAllText(TempPath, json);
        File.Move(TempPath, _filePath, true);

        // Advance in-memory state only after disk success so Current never leads what a reload sees.
        Current = settings;
        SettingsChanged?.Invoke(settings);
    }

    private static AppSettings? TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            // ReSharper disable once InconsistentlySynchronizedField -- s_jsonOptions is static readonly (immutable reference); _gate guards file I/O and Current, not this field.
            var settings = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);
            if (settings is null)
            {
                return null;
            }

            settings = ApplyHistoryRetentionMigration(settings, json);
            settings = ApplyAccelerationMigration(settings, json);
            return settings;
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to load settings from {path}: {ex.Message}");
            return null;
        }
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
