using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

public sealed class FileMemoryPlugin : IMemoryStoragePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private IPluginHostServices? _host;
    private string? _filePath;
    private List<MemoryEntry>? _entries;
    private bool _loadFailed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string PluginId => "com.typewhisper.file-memory";
    public string PluginName => "File Memory";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _filePath = Path.Join(host.PluginDataDirectory, "memories.json");
        _host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _host = null;
        _entries = null;
        _loadFailed = false;
        return Task.CompletedTask;
    }

    public async Task StoreAsync(string content, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var current = await LoadEntriesAsync(ct);

            if (current.Any(e => e.Content == content))
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            var next = new List<MemoryEntry>(current)
            {
                new(content, DateTime.UtcNow)
            };
            await SaveEntriesAsync(next, ct);
            _entries = next;
            _host?.Log(PluginLogLevel.Debug, $"Stored memory (total={next.Count})");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);

            return entries
                .Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt)
                .Take(maxResults)
                .Select(e => e.Content)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            return entries.Select(e => e.Content).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string content, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var current = await LoadEntriesAsync(ct);
            var next = new List<MemoryEntry>(current);
            var removed = next.RemoveAll(e => e.Content == content);

            if (removed == 0 && !_loadFailed)
                return;

            await SaveEntriesAsync(next, ct);
            _entries = next;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var current = await LoadEntriesAsync(ct);
            if (current.Count == 0 && !_loadFailed)
                return;

            var next = new List<MemoryEntry>(current);
            next.Clear();
            await SaveEntriesAsync(next, ct);
            _entries = next;
            _host?.Log(PluginLogLevel.Info, "All memories cleared");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            return entries.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<MemoryEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (_entries is not null)
            return _entries;

        if (_filePath is null)
            throw new InvalidOperationException("Plugin not activated");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(_filePath, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _loadFailed = false;
            _entries = [];
            return _entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            _host?.Log(
                PluginLogLevel.Warning,
                $"Failed to read memories; saves are disabled to protect the existing file: {ex.Message}"
            );
            return [];
        }

        try
        {
            _entries =
                JsonSerializer.Deserialize<List<MemoryEntry>>(json, JsonOptions)
                ?? throw new JsonException("The memory file contained null JSON.");
            _loadFailed = false;
            return _entries;
        }
        catch (JsonException ex)
        {
            var brokenPath =
                $"{_filePath}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            try
            {
                File.Move(_filePath, brokenPath);
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Failed to parse memories; the corrupt file was preserved as '{brokenPath}': {ex.Message}"
                );
                _loadFailed = false;
                _entries = [];
                return _entries;
            }
            catch (Exception preserveEx)
            {
                _host?.Log(
                    PluginLogLevel.Warning,
                    $"Failed to preserve corrupt memory file as '{brokenPath}': {preserveEx.Message}"
                );

                if (File.Exists(_filePath))
                {
                    _loadFailed = true;
                    return [];
                }

                _loadFailed = false;
                _entries = [];
                return _entries;
            }
        }
    }

    private async Task SaveEntriesAsync(List<MemoryEntry> entries, CancellationToken ct)
    {
        if (_filePath is null)
            throw new InvalidOperationException("Plugin not activated");

        if (_loadFailed)
        {
            var message =
                $"Refusing to overwrite '{_filePath}' because the previous load failed.";
            _host?.Log(PluginLogLevel.Error, message);
            throw new IOException(message);
        }

        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(entries, JsonOptions);

            if (!OperatingSystem.IsWindows())
            {
                // Owner-only on *every* write, including the first: neither the umask (0644 under
                // a typical 022) nor an existing permissive mode may widen dictated memories.
                // Set before the content is written so it is never briefly readable.
                using (
                    new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1
                    )
                )
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            await File.WriteAllTextAsync(tempPath, json, ct);

            // One atomic rename for both the create and the replace case; sampling whether the
            // destination existed first would only add races in each direction. Moves the temp
            // file's inode and its 0600 into place, repairing a world-readable legacy file.
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }

            _host?.Log(PluginLogLevel.Error, $"Failed to save memories: {ex.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    private sealed record MemoryEntry(string Content, DateTime CreatedAt);
}
