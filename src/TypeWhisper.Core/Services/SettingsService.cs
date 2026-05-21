using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
        // Try primary settings file
        var result = TryLoadFrom(_filePath);
        if (result is not null)
        {
            Current = result;
            return Current;
        }

        // Primary failed — try backup
        if (File.Exists(BackupPath))
        {
            LogWarning("Primary settings corrupt or missing, trying backup.");
            result = TryLoadFrom(BackupPath);
            if (result is not null)
            {
                Current = result;
                // Restore backup as primary
                try
                {
                    File.Copy(BackupPath, _filePath, true);
                }
                catch
                {
                    /* best effort */
                }

                return Current;
            }
        }

        LogWarning("No valid settings found, using defaults.");
        Current = AppSettings.Default;
        return Current;
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Backup current settings before overwriting
        if (File.Exists(_filePath))
        {
            try
            {
                File.Copy(_filePath, BackupPath, true);
            }
            catch
            {
                /* best effort */
            }
        }

        // Atomic write: serialize to .tmp, then move over primary
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(TempPath, json);
        File.Move(TempPath, _filePath, true);

        // Only advance in-memory state after disk persistence succeeds, so an
        // I/O failure doesn't leave Current ahead of what reload sees.
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings is null ? null : ApplyHistoryRetentionMigration(settings, json);
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to load settings from {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Migrates settings written by older builds that stored retention as
    ///     <c>historyRetentionDays</c> (int) rather than the current
    ///     <c>historyRetentionMode</c> / <c>historyRetentionMinutes</c> pair.
    ///     The magic value 9999 was the sentinel for "keep forever".
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
                    HistoryRetentionMinutes = legacyDays.Value * 24 * 60
                },
                _ => settings with
                {
                    HistoryRetentionMode = AppSettings.Default.HistoryRetentionMode,
                    HistoryRetentionMinutes = AppSettings.Default.HistoryRetentionMinutes
                }
            };
        }

        if (
            settings.HistoryRetentionMode == HistoryRetentionMode.Duration
            && settings.HistoryRetentionMinutes <= 0
        )
        {
            return settings with
            {
                HistoryRetentionMinutes = AppSettings.Default.HistoryRetentionMinutes
            };
        }

        return settings;
    }

    private static void LogWarning(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [SettingsService] {message}";
        Debug.WriteLine(line);

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TypeWhisper",
                "Logs"
            );
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "settings.log"), line + Environment.NewLine);
        }
        catch
        {
            /* logging must never throw */
        }
    }
}