using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Claude;

public sealed partial class ClaudePlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.anthropic.com";

    // Anthropic requires an anthropic-version header on every request; this is
    // the stable version that covers the Messages API used here.
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;
    private bool _streamResponses = true;

    public ClaudePlugin()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
    {
    }

    internal ClaudePlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.claude";
    public string PluginName => "Claude";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderName => "Claude";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
    [
        new PluginModelInfo("claude-sonnet-4-20250514", "Claude Sonnet 4"),
        new PluginModelInfo("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
    ];

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var requestBody = new
        {
            model,
            max_tokens = 2048,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userText } },
        };

        var json = JsonSerializer.Serialize(
            requestBody,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _host?.Log(
                PluginLogLevel.Error,
                $"Anthropic API error {response.StatusCode}: {responseBody}"
            );
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {responseBody}"
            );
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement.GetProperty("content");
        if (content.GetArrayLength() == 0)
            throw new InvalidOperationException("Anthropic API returned empty content array");

        return content[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Anthropic API returned null text");
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        if (!_streamResponses)
        {
            yield return await ProcessAsync(systemPrompt, userText, model, ct);
            yield break;
        }

        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.ApiKeyNotConfigured"));

        var requestBody = new
        {
            model,
            max_tokens = 2048,
            stream = true,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userText } },
        };

        var json = JsonSerializer.Serialize(
            requestBody,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead so deltas surface as they arrive instead of
        // buffering the whole SSE body (the batch path reads the body to a string).
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _host?.Log(
                PluginLogLevel.Error,
                $"Anthropic API error {response.StatusCode}: {errorBody}"
            );
            throw new HttpRequestException(
                $"Anthropic API returned {(int)response.StatusCode}: {errorBody}"
            );
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // The Anthropic Messages stream has no [DONE] sentinel; it ends with a
        // message_stop frame and then EOF, so the loop runs until ReadLineAsync
        // returns null.
        while (await reader.ReadLineAsync(ct) is { } rawLine)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var payload = line[6..];

            // A Messages stream returns 200 then can fail mid-flight via an
            // `event: error` frame. Throw so LlmStreamPump faults and the caller
            // falls back to batch, instead of committing the partial deltas seen
            // so far as a successful result.
            if (ParseStreamError(payload) is { } error)
                throw new InvalidOperationException(error);

            if (ParseStreamDelta(payload) is { Length: > 0 } delta)
                yield return delta;
        }
    }

    /// <summary>
    ///     Extracts the incremental text from a single Anthropic Messages SSE
    ///     <c>data:</c> payload — a <c>content_block_delta</c> frame whose
    ///     <c>delta.type</c> is <c>text_delta</c>. Returns <c>null</c> for any
    ///     other frame type or an unparseable payload. Reflection-free (A18) via
    ///     <see cref="JsonDocument" />.
    /// </summary>
    internal static string? ParseStreamDelta(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && typeEl.GetString() == "content_block_delta"
                && root.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("type", out var deltaType)
                && deltaType.ValueKind == JsonValueKind.String
                && deltaType.GetString() == "text_delta"
                && delta.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns a provider error message when a single Messages SSE
    ///     <c>data:</c> payload is an <c>error</c> frame, otherwise <c>null</c>.
    ///     Used by the streaming reader to surface a post-200 stream failure as a
    ///     thrown exception. Reflection-free (A18) via <see cref="JsonDocument" />.
    /// </summary>
    internal static string? ParseStreamError(string dataPayload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(dataPayload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() != "error")
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "Anthropic streaming error.";
            }

            return "Anthropic streaming error.";
        }
    }

    internal bool IsConfigured => !string.IsNullOrEmpty(_apiKey);
    internal string? ApiKey => _apiKey;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        // Trim defensively at the internal entry too: SetSettingValueAsync
        // already trims, but a future direct caller could re-introduce
        // trailing whitespace that breaks the x-api-key header.
        var trimmed = apiKey?.Trim();
        _apiKey = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", trimmed);

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal bool ValidateApiKeyFormat(string apiKey)
    {
        return !string.IsNullOrWhiteSpace(apiKey) && apiKey.StartsWith("sk-ant-");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                Key: "api-key",
                Label: Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Placeholder: "sk-ant-...",
                Description: Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                Key: LlmStreamingSettings.StreamResponsesSettingKey,
                Label: Loc.L("Settings.StreamResponses"),
                Description: Loc.L("Settings.StreamResponsesDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => _apiKey,
                LlmStreamingSettings.StreamResponsesSettingKey
                    => _streamResponses ? "true" : "false",
                _ => null,
            }
        );

    public async Task SetSettingValueAsync(
        string key,
        string? value,
        CancellationToken ct = default
    )
    {
        switch (key)
        {
            case "api-key":
                // Normalize whitespace once — pasted keys often pick up trailing
                // newlines or spaces that break the x-api-key header.
                await SetApiKeyAsync(value?.Trim() ?? string.Empty);
                break;
            case LlmStreamingSettings.StreamResponsesSettingKey:
                SetStreamResponses(ParseBool(value));
                break;
        }
    }

    private void SetStreamResponses(bool enabled)
    {
        _streamResponses = enabled;
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return Task.FromResult<PluginSettingsValidationResult?>(
                new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"))
            );

        var valid = ValidateApiKeyFormat(_apiKey);
        return Task.FromResult<PluginSettingsValidationResult?>(
            valid
                ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyFormatValid"))
                : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyFormatInvalid"))
        );
    }
}
