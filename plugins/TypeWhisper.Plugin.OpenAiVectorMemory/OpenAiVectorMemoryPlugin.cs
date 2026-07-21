// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiVectorMemory;

public sealed class OpenAiVectorMemoryPlugin : IMemoryStoragePlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string EmbeddingModel = "text-embedding-3-small";
    private const string EmbeddingUrl = "https://api.openai.com/v1/embeddings";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _filePath;
    private List<VectorMemoryEntry>? _entries;

    public string PluginId => "com.typewhisper.openai-vector-memory";
    public string PluginName => "OpenAI Vector Memory";
    public string PluginVersion => "1.0.0";

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _filePath = Path.Join(host.PluginDataDirectory, "vector-memories.json");
        // Normalize on load: legacy stored keys may carry trailing whitespace
        // from before SetSettingValueAsync started trimming.
        var stored = await host.LoadSecretAsync("api-key");
        var trimmed = stored?.Trim();
        _apiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        host.Log(PluginLogLevel.Info, $"Activated (configured={!string.IsNullOrEmpty(_apiKey)})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        _entries = null;
        return Task.CompletedTask;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "api-key",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "sk-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "api-key" ? _apiKey : null);

    public async Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        if (key != "api-key")
            return;

        // Normalize once and reuse so whitespace-padded keys aren't stored or
        // treated as "configured" in memory, persistence, or validation.
        var trimmed = value?.Trim();
        _apiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", trimmed);
        }
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default) =>
        Task.FromResult<PluginSettingsValidationResult?>(
            string.IsNullOrWhiteSpace(_apiKey)
                ? new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"))
                : new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyConfigured"))
        );

    public async Task StoreAsync(string content, CancellationToken ct)
    {
        EnsureConfigured();

        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);

            if (entries.Any(e => e.Content == content))
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            var embedding = await GetEmbeddingAsync(content, ct);
            entries.Add(new VectorMemoryEntry(content, embedding, DateTime.UtcNow));
            await SaveEntriesAsync(ct);
            _host?.Log(PluginLogLevel.Debug, $"Stored vector memory (total={entries.Count})");
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
        EnsureConfigured();

        await _lock.WaitAsync(ct);
        try
        {
            var entries = await LoadEntriesAsync(ct);
            if (entries.Count == 0)
                return [];

            var queryEmbedding = await GetEmbeddingAsync(query, ct);

            return entries
                .Select(e => (Entry: e, Similarity: CosineSimilarity(queryEmbedding, e.Embedding)))
                .OrderByDescending(x => x.Similarity)
                .Take(maxResults)
                .Select(x => x.Entry.Content)
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
            var entries = await LoadEntriesAsync(ct);
            // Snapshot before mutating so a SaveEntriesAsync failure doesn't
            // leave the in-memory cache out of sync with the on-disk file —
            // a later StoreAsync would otherwise persist the deleted state.
            var snapshot = new List<VectorMemoryEntry>(entries);
            var removed = entries.RemoveAll(e => e.Content == content);

            if (removed > 0)
            {
                try
                {
                    await SaveEntriesAsync(ct);
                }
                catch
                {
                    _entries = snapshot;
                    throw;
                }
            }
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
            var entries = await LoadEntriesAsync(ct);
            var snapshot = new List<VectorMemoryEntry>(entries);
            entries.Clear();

            try
            {
                await SaveEntriesAsync(ct);
            }
            catch
            {
                _entries = snapshot;
                throw;
            }

            _host?.Log(PluginLogLevel.Info, "All vector memories cleared");
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

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var requestBody = JsonSerializer.Serialize(new { model = EmbeddingModel, input = text });

        using var request = new HttpRequestMessage(HttpMethod.Post, EmbeddingUrl);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Embedding requests carry transcribed user content; on rate-limit
            // or content errors the response body can echo input fragments
            // back. Keep both the plugin log and the thrown exception to a
            // stable status + reason so neither surface leaks user text.
            _host?.Log(
                PluginLogLevel.Error,
                $"Embedding API error {(int)response.StatusCode} ({response.ReasonPhrase})"
            );
            throw new HttpRequestException(
                $"OpenAI Embedding API returned {(int)response.StatusCode}: {response.ReasonPhrase}"
            );
        }

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");

        var embedding = new float[embeddingArray.GetArrayLength()];
        var i = 0;
        foreach (var element in embeddingArray.EnumerateArray())
        {
            embedding[i++] = element.GetSingle();
        }

        return embedding;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        double dot = 0,
            normA = 0,
            normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private async Task<List<VectorMemoryEntry>> LoadEntriesAsync(CancellationToken ct)
    {
        if (_entries is not null)
            return _entries;

        if (_filePath is null)
            throw new InvalidOperationException("Plugin not activated");

        if (File.Exists(_filePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                _entries =
                    JsonSerializer.Deserialize<List<VectorMemoryEntry>>(json, s_jsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                // Surface the failure instead of swallowing it: callers like
                // StoreAsync/DeleteAsync would otherwise write back an empty
                // list and clobber a corrupt-but-recoverable file.
                _host?.Log(PluginLogLevel.Warning, $"Failed to load vector memories: {ex.Message}");
                throw;
            }
        }
        else
        {
            _entries = [];
        }

        return _entries;
    }

    private async Task SaveEntriesAsync(CancellationToken ct)
    {
        if (_filePath is null || _entries is null)
            return;

        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_entries, s_jsonOptions);

        // Write to a sibling temp file and atomically replace, so a crash
        // mid-write can't leave the vector store truncated.
        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json, ct);
            if (File.Exists(_filePath))
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _filePath);
        }
        catch
        {
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    private void EnsureConfigured()
    {
        // Match ValidateAsync's IsNullOrWhiteSpace check so a whitespace-only secret
        // (e.g. legacy data in the secret store) is treated as missing in both paths.
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException(
                "OpenAI API key not configured. Set it in plugin settings."
            );
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }

    private sealed record VectorMemoryEntry(string Content, float[] Embedding, DateTime CreatedAt);
}
