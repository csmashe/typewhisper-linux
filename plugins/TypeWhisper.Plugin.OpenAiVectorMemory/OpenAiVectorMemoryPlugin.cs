using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiVectorMemory;

public sealed class OpenAiVectorMemoryPlugin : IMemoryStoragePlugin, IPluginSettingsProvider
{
    private const string EmbeddingModel = "text-embedding-3-small";
    private const string EmbeddingUrl = "https://api.openai.com/v1/embeddings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IPluginHostServices? _host;
    private string? _apiKey;
    private string? _filePath;
    private List<VectorMemoryEntry>? _entries;

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.openai-vector-memory";
    public string PluginName => "OpenAI Vector Memory";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _filePath = Path.Combine(host.PluginDataDirectory, "vector-memories.json");
        _apiKey = await host.LoadSecretAsync("api-key");
        host.Log(PluginLogLevel.Info, $"Activated (configured={!string.IsNullOrEmpty(_apiKey)})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        _entries = null;
        return Task.CompletedTask;
    }

    // IPluginSettingsProvider

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new(
            Key: "api-key",
            Label: "API key",
            IsSecret: true,
            Placeholder: "sk-...",
            Description: "OpenAI API key used to generate embeddings for vector memory.")
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "api-key" ? _apiKey : null);

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (key != "api-key")
            return;

        _apiKey = string.IsNullOrWhiteSpace(value) ? null : value;

        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(value))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", value);
        }
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default) =>
        Task.FromResult<PluginSettingsValidationResult?>(
            string.IsNullOrWhiteSpace(_apiKey)
                ? new PluginSettingsValidationResult(false, "Enter an API key first.")
                : new PluginSettingsValidationResult(true, "API key configured."));

    // IMemoryStoragePlugin

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

    public async Task<IReadOnlyList<string>> SearchAsync(string query, int maxResults = 5, CancellationToken ct = default)
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
            var removed = entries.RemoveAll(e => e.Content == content);

            if (removed > 0)
                await SaveEntriesAsync(ct);
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
            entries.Clear();
            await SaveEntriesAsync(ct);
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

    // Embedding API

    private async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model = EmbeddingModel,
            input = text,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, EmbeddingUrl);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _host?.Log(PluginLogLevel.Error, $"Embedding API error {response.StatusCode}: {responseBody}");
            throw new HttpRequestException(
                $"OpenAI Embedding API returned {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var embeddingArray = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");

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

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            normA += a[i] * (double)a[i];
            normB += b[i] * (double)b[i];
        }

        var denominator = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    // Persistence

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
                _entries = JsonSerializer.Deserialize<List<VectorMemoryEntry>>(json, JsonOptions) ?? [];
            }
            catch (Exception ex)
            {
                _host?.Log(PluginLogLevel.Warning, $"Failed to load vector memories: {ex.Message}");
                _entries = [];
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

        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenAI API key not configured. Set it in plugin settings.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _lock.Dispose();
    }

    private sealed record VectorMemoryEntry(string Content, float[] Embedding, DateTime CreatedAt);
}
