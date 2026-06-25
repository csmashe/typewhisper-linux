// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides persistent memory storage for extracted facts.
///     Memory entries are key facts extracted from transcriptions via LLM.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IMemoryStoragePlugin : ITypeWhisperPlugin
{
    /// <summary>Stores a memory entry. Duplicate content should be deduplicated by the plugin.</summary>
    Task StoreAsync(string content, CancellationToken ct = default);

    /// <summary>Searches stored memories by query. Returns relevant entries ranked by relevance.</summary>
    Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    );

    /// <summary>Returns all stored memory entries.</summary>
    // ReSharper disable once UnusedMember.Global
    Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Deletes a specific memory entry by its content.</summary>
    // ReSharper disable once UnusedMember.Global
    Task DeleteAsync(string content, CancellationToken ct = default);

    /// <summary>Deletes all stored memory entries.</summary>
    // ReSharper disable once UnusedMember.Global
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>Number of stored memory entries.</summary>
    // ReSharper disable once UnusedMember.Global
    Task<int> CountAsync(CancellationToken ct = default);
}
