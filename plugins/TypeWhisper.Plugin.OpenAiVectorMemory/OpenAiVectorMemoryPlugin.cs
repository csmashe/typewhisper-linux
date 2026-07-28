// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Collections.Immutable;
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

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private IPluginStateStore<ImmutableArray<VectorMemoryEntry>>? _store;

    // Store commits are atomic, but an operation spans a read, an embedding request, and a
    // commit; without this gate, a concurrent clear/delete could be overwritten by a lagging commit.
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    // ReSharper disable once UnusedMember.Global -- the host instantiates the plugin through this public parameterless constructor via reflection, which the analyzer cannot see.
    public OpenAiVectorMemoryPlugin()
        : this(new HttpClientHandler())
    {
    }

    internal OpenAiVectorMemoryPlugin(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

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
        _store = host.OpenStateStore<ImmutableArray<VectorMemoryEntry>>(
            "vector-memories.json",
            static () => [],
            new PluginStateStoreOptions
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = PluginStateCorruptFilePolicy.Throw,
            }
        );
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
        _store = null;
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
        var nextApiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;

        if (_host is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", trimmed);
        }

        _apiKey = nextApiKey;
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

        var store = GetStore();
        await _operationGate.WaitAsync(ct);
        try
        {
            var existing = await store.ReadAsync(ct);
            if (existing.Any(e => e.Content == content))
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            var embedding = await GetEmbeddingAsync(content, ct);
            var added = false;
            var committed = await store.UpdateAsync(
                current =>
                {
                    if (current.Any(e => e.Content == content))
                    {
                        return current;
                    }

                    added = true;
                    return current.Add(
                        new VectorMemoryEntry(content, embedding, DateTime.UtcNow)
                    );
                },
                ct
            );
            if (!added)
            {
                _host?.Log(PluginLogLevel.Debug, "Duplicate memory skipped");
                return;
            }

            _host?.Log(
                PluginLogLevel.Debug,
                $"Stored vector memory (total={committed.Length})"
            );
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default
    )
    {
        EnsureConfigured();

        var store = GetStore();
        // Gated like the mutations: a search reads then embeds, and matching against a
        // pre-clear snapshot would surface memory the user has already deleted.
        await _operationGate.WaitAsync(ct);
        try
        {
            var entries = await store.ReadAsync(ct);
            if (entries.IsEmpty)
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
            _operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct)
    {
        var entries = await GetStore().ReadAsync(ct);
        return entries.Select(e => e.Content).ToList();
    }

    public async Task DeleteAsync(string content, CancellationToken ct)
    {
        var store = GetStore();
        await _operationGate.WaitAsync(ct);
        try
        {
            await store.UpdateAsync(
                current =>
                {
                    var next = current.Where(e => e.Content != content).ToImmutableArray();
                    return next.Length == current.Length ? current : next;
                },
                ct
            );
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        var cleared = false;
        var store = GetStore();
        await _operationGate.WaitAsync(ct);
        try
        {
            await store.UpdateAsync(
                current =>
                {
                    cleared = !current.IsEmpty;
                    return cleared ? [] : current;
                },
                ct
            );
        }
        finally
        {
            _operationGate.Release();
        }

        if (cleared)
        {
            _host?.Log(PluginLogLevel.Info, "All vector memories cleared");
        }
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        return (await GetStore().ReadAsync(ct)).Length;
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
        _operationGate.Dispose();
    }

    private IPluginStateStore<ImmutableArray<VectorMemoryEntry>> GetStore() =>
        _store ?? throw new InvalidOperationException("Plugin not activated");

    // ReSharper disable once NotAccessedPositionalProperty.Local -- CreatedAt is persisted metadata in the serialized entry shape, not dead code.
    private sealed record VectorMemoryEntry(
        string Content,
        float[] Embedding,
        DateTime CreatedAt
    );
}
