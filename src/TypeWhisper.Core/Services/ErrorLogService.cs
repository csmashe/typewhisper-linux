using System.Collections.Immutable;
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
    private readonly AtomicJsonStore<ImmutableArray<ErrorLogEntry>> _store;

    public ErrorLogService(string dataDirectory)
    {
        var logFilePath = Path.Join(dataDirectory, "error-log.json");
        _store = new AtomicJsonStore<ImmutableArray<ErrorLogEntry>>(
            logFilePath,
            static () => [],
            new AtomicJsonStoreOptions<ImmutableArray<ErrorLogEntry>>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = DeserializeEntries,
            }
        );
        _ = ReadEntries();
    }

    public IReadOnlyList<ErrorLogEntry> Entries => ReadEntries().ToArray();

    /// <summary>
    ///     A read failure must not stop the app, so it degrades to empty. Safe: a later
    ///     <see cref="AddEntry" /> reloads through the store, which still refuses to
    ///     overwrite an unreadable primary.
    /// </summary>
    private ImmutableArray<ErrorLogEntry> ReadEntries()
    {
        try
        {
            return _store.Current;
        }
        catch
        {
            return [];
        }
    }

    public event Action? EntriesChanged;

    public void AddEntry(string message, string category = ErrorCategory.General)
    {
        var entry = ErrorLogEntry.Create(message, category);

        try
        {
            _store.Update(
                current =>
                {
                    var next = current.Insert(0, entry);
                    return next.Length > MaxEntries
                        ? next.RemoveRange(MaxEntries, next.Length - MaxEntries)
                        : next;
                }
            );
        }
        catch
        {
            // Best-effort persistence: do not publish the entry or surface the failure.
            return;
        }

        EntriesChanged?.Invoke();
    }

    public void ClearAll()
    {
        bool changed;
        try
        {
            _store.Update(static current => current.IsEmpty ? current : [], out changed);
        }
        catch
        {
            // Best-effort persistence: keep the prior committed entries.
            return;
        }

        // Outside the catch, as in AddEntry: a throwing subscriber is not a persistence failure.
        if (changed)
        {
            EntriesChanged?.Invoke();
        }
    }

    public string ExportDiagnostics()
    {
        var snapshot = ReadEntries();

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
                timezone = TimeZoneInfo.Local.Id,
            },
            error_count = snapshot.Length,
            errors = snapshot.Select(e => new
            {
                timestamp = e.Timestamp.ToString("o"), category = e.Category, message = e.Message,
            }),
        };

        return JsonSerializer.Serialize(report, s_jsonOptions);
    }

    private static ImmutableArray<ErrorLogEntry> DeserializeEntries(string json)
    {
        var entries =
            JsonSerializer.Deserialize<ImmutableArray<ErrorLogEntry>>(json, s_jsonOptions);
        if (entries.IsDefault)
        {
            throw new JsonException("Error-log JSON deserialized to null.");
        }

        return entries.Length > MaxEntries
            ? entries.RemoveRange(MaxEntries, entries.Length - MaxEntries)
            : entries;
    }

    private static string GetAppVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        return asm?.GetName().Version?.ToString() ?? "unknown";
    }
}
