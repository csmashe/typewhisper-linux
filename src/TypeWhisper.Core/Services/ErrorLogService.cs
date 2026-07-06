using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IErrorLogService" />: keeps a capped, newest-first ring buffer of
///     error entries persisted as JSON, and exports them with app/environment metadata for bug reports.
/// </summary>
public sealed class ErrorLogService : IErrorLogService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private const int MaxEntries = 200;
    private readonly List<ErrorLogEntry> _entries = [];
    private readonly Lock _lock = new();

    private readonly string _logFilePath;

    public ErrorLogService(string dataDirectory)
    {
        _logFilePath = Path.Join(dataDirectory, "error-log.json");
        LoadFromDisk();
    }

    public IReadOnlyList<ErrorLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public event Action? EntriesChanged;

    public void AddEntry(string message, string category = ErrorCategory.General)
    {
        var entry = ErrorLogEntry.Create(message, category);

        lock (_lock)
        {
            _entries.Insert(0, entry);

            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            // Persist inside the lock so two near-simultaneous AddEntry calls
            // can't have the older snapshot overwrite the newer one on disk.
            SaveToDisk();
        }

        EntriesChanged?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveToDisk();
        }

        EntriesChanged?.Invoke();
    }

    public string ExportDiagnostics()
    {
        List<ErrorLogEntry> snapshot;
        lock (_lock)
        {
            snapshot = [.. _entries];
        }

        var report = new
        {
            exported_at = DateTime.UtcNow.ToString("o"),
            app = new
            {
                version = GetAppVersion(),
                platform = RuntimeInformation.OSDescription,
                os_version = Environment.OSVersion.VersionString,
                dotnet_version = Environment.Version.ToString(),
                locale = CultureInfo.CurrentCulture.Name,
                timezone = TimeZoneInfo.Local.Id
            },
            error_count = snapshot.Count,
            errors = snapshot.Select(e => new
            {
                timestamp = e.Timestamp.ToString("o"), category = e.Category, message = e.Message
            })
        };

        return JsonSerializer.Serialize(report, s_jsonOptions);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_logFilePath))
            {
                return;
            }

            var json = File.ReadAllText(_logFilePath);
            var entries = JsonSerializer.Deserialize<List<ErrorLogEntry>>(json);
            if (entries is null)
            {
                return;
            }

            // Trim on load: an older build or hand-edited file may exceed MaxEntries.
            if (entries.Count > MaxEntries)
            {
                entries = entries.GetRange(0, MaxEntries);
            }

            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(entries);
            }
        }
        catch
        {
            // Corrupted or unreadable log — start fresh rather than surfacing an error about the error log
        }
    }

    private void SaveToDisk()
    {
        // Caller must hold _lock so the on-disk snapshot stays in step with
        // the in-memory list; two concurrent writers could otherwise interleave.
        try
        {
            var json = JsonSerializer.Serialize(_entries, s_jsonOptions);

            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic temp-file + replace so a crash mid-write can't corrupt error-log.json.
            var tempPath = _logFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(_logFilePath))
                {
                    File.Replace(tempPath, _logFilePath, null);
                }
                else
                {
                    File.Move(tempPath, _logFilePath);
                }
            }
            catch
            {
                if (!File.Exists(tempPath))
                {
                    throw;
                }

                try { File.Delete(tempPath); }
                catch
                {
                    /* best effort */
                }

                throw;
            }
        }
        catch
        {
            // Best-effort persistence — silently discard save failures so callers are unaffected
        }
    }

    private static string GetAppVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetName().Version?.ToString() ?? "unknown";
    }
}