using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class ErrorLogService : IErrorLogService
{
    private const int MaxEntries = 200;
    private readonly List<ErrorLogEntry> _entries = [];
    private readonly Lock _lock = new();

    private readonly string _logFilePath;

    public ErrorLogService(string dataDirectory)
    {
        _logFilePath = Path.Combine(dataDirectory, "error-log.json");
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

    public void AddEntry(string message, string category = "general")
    {
        var entry = ErrorLogEntry.Create(message, category);

        lock (_lock)
        {
            _entries.Insert(0, entry);

            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }

        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        SaveToDisk();
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
                timestamp = e.Timestamp.ToString("o"),
                category = e.Category,
                message = e.Message
            })
        };

        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
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
            if (entries is not null)
            {
                // Entries are persisted newest-first (AddEntry inserts at index 0).
                // Trim to MaxEntries on load so a file written by an older build
                // with a higher cap — or hand-edited — doesn't leave the in-memory
                // cache permanently over budget until the next AddEntry trims it down.
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
        }
        catch
        {
            // Corrupted or unreadable log — start fresh rather than surfacing an error about the error log
        }
    }

    private void SaveToDisk()
    {
        try
        {
            List<ErrorLogEntry> snapshot;
            lock (_lock)
            {
                snapshot = [.. _entries];
            }

            var json = JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions { WriteIndented = true }
            );

            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_logFilePath, json);
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