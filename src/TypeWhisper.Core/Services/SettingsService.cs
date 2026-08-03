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

    // Publication happens outside _gate (handlers must not run under the write lock) but still in
    // commit order, or a preempted publisher could deliver its older snapshot after a newer
    // commit. Both fields are guarded by _gate; see PublishPendingChanges.
    private readonly Queue<AppSettings> _pendingNotifications = new();
    private bool _publishing;
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
        // Under _gate for the whole read: Load both writes Current and (on the backup path) copies
        // over the primary file, so an unsynchronized read could clobber a concurrent Save.
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    public AppSettings Reload()
    {
        AppSettings committed;
        lock (_gate)
        {
            committed = SaveLocked(LoadLocked());
        }

        PublishPendingChanges();
        return committed;
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

        PublishPendingChanges();
    }

    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings committed;
        lock (_gate)
        {
            committed = SaveLocked(mutate(Current));
        }

        PublishPendingChanges();
        return committed;
    }

    /// <summary>
    ///     Drains committed snapshots to <see cref="SettingsChanged" /> in commit order. No lock is
    ///     held while a subscriber runs — a handler that saves, or waits on a thread that saves,
    ///     must not deadlock — so a single active drainer is elected instead. A writer that finds a
    ///     drain already running (another thread, or this thread re-entering from a handler) leaves
    ///     its snapshot queued and returns, keeping delivery ordered and non-recursive.
    /// </summary>
    private void PublishPendingChanges()
    {
        lock (_gate)
        {
            if (_publishing)
            {
                return;
            }

            _publishing = true;
        }

        try
        {
            while (true)
            {
                AppSettings next;
                lock (_gate)
                {
                    if (_pendingNotifications.Count == 0)
                    {
                        // Resign in the same acquisition that observes the empty queue. Clearing
                        // later leaves a window where a writer enqueues, sees _publishing still
                        // true, declines to drain, and strands its notification.
                        _publishing = false;
                        return;
                    }

                    next = _pendingNotifications.Dequeue();
                }

                // Per subscriber, not per multicast invoke: one throwing handler would otherwise
                // starve every handler after it. The drainer may also be carrying another writer's
                // snapshot, so a failure must not escape and fail that already-succeeded Save.
                foreach (var subscriber in
                         SettingsChanged?.GetInvocationList() ?? [])
                {
                    try
                    {
                        ((Action<AppSettings>)subscriber)(next);
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"A SettingsChanged subscriber threw: {ex}");
                    }
                }
            }
        }
        catch
        {
            // Backstop for an abnormal exit (the subscriber chain is already guarded above):
            // never leave the flag set, or publication stops for the process lifetime.
            lock (_gate)
            {
                _publishing = false;
            }

            throw;
        }
    }

    // Queues the committed snapshot instead of raising SettingsChanged here: handlers must never
    // run under _gate. Callers publish via PublishPendingChanges once the lock is released.
    private AppSettings SaveLocked(AppSettings settings)
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
        _pendingNotifications.Enqueue(settings);
        return settings;
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
