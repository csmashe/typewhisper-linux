using System.Collections.Immutable;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FileMemory;

public sealed class FileMemoryPlugin : IMemoryStoragePlugin
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private IPluginHostServices? _host;
    private IPluginStateStore<ImmutableArray<MemoryEntry>>? _store;

    public string PluginId => "com.typewhisper.file-memory";
    public string PluginName => "File Memory";
    public string PluginVersion => PluginBuildInfo.Version;

    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _store = host.OpenStateStore<ImmutableArray<MemoryEntry>>(
            "memories.json",
            static () => [],
            new PluginStateStoreOptions
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = PluginStateCorruptFilePolicy.PreserveAndReset,
            }
        );
        _host.Log(PluginLogLevel.Info, "Activated");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync()
    {
        _host = null;
        _store = null;
        return Task.CompletedTask;
    }

    public async Task StoreAsync(string content, CancellationToken ct)
    {
        var added = false;
        var committed = await GetStore().UpdateAsync(
            current =>
            {
                if (current.Any(e => e.Content == content))
                {
                    return current;
                }

                added = true;
                return current.Add(new MemoryEntry(content, DateTime.UtcNow));
            },
            ct
        );
        if (!added)
        {
            _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
            return;
        }

        _host?.Log(PluginLogLevel.Debug, $"Stored memory (total={committed.Length})");
    }

    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        var entries = await GetStore().ReadAsync(ct);
        return entries
                .Where(e => e.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt)
                .Take(maxResults)
                .Select(e => e.Content)
                .ToList();
    }

    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct)
    {
        var entries = await GetStore().ReadAsync(ct);
        return entries.Select(e => e.Content).ToList();
    }

    public async Task DeleteAsync(string content, CancellationToken ct)
    {
        await GetStore().UpdateAsync(
            current =>
            {
                var next = current.Where(e => e.Content != content).ToImmutableArray();
                return next.Length == current.Length ? current : next;
            },
            ct
        );
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        var cleared = false;
        await GetStore().UpdateAsync(
            current =>
            {
                cleared = !current.IsEmpty;
                return cleared ? [] : current;
            },
            ct
        );
        if (cleared)
        {
            _host?.Log(PluginLogLevel.Info, "All memories cleared");
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        return (await GetStore().ReadAsync(ct)).Length;
    }

    private IPluginStateStore<ImmutableArray<MemoryEntry>> GetStore() =>
        _store ?? throw new InvalidOperationException("Plugin not activated");

    public void Dispose() { }

    private sealed record MemoryEntry(string Content, DateTime CreatedAt);
}
