// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Claude;

public sealed class ClaudePlugin : ILlmProviderPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.anthropic.com";

    // Anthropic requires an anthropic-version header on every request; this is
    // the stable version that covers the Messages API used here.
    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string FetchedModelsSettingKey = "fetchedModels";
    private const int ModelPageLimit = 1000;
    private const int MaxModelPages = 20;
    private const int MaxModelIdLength = 256;

    // Default and fallback order from upstream typewhisper-win #467/#468 (ac798823).
    internal const string DefaultModelId = "claude-sonnet-5";

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackModels =
    [
        new(DefaultModelId, "Claude Sonnet 5"),
        new("claude-opus-5", "Claude Opus 5"),
        new("claude-sonnet-4-6", "Claude Sonnet 4.6"),
        new("claude-haiku-4-5-20251001", "Claude Haiku 4.5"),
    ];

    private readonly HttpClient _httpClient;
    private readonly Lock _catalogLock = new();
    private IPluginHostServices? _host;
    private bool _streamResponses = true;

    // Bumped on key change or deactivation so an in-flight refresh cannot commit.
    private long _catalogGeneration;

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
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        lock (_catalogLock)
            SetCatalog(NormalizeModels(host.GetSetting<List<ClaudeModel>>(FetchedModelsSettingKey) ?? []));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        lock (_catalogLock)
        {
            _catalogGeneration++;
            _host = null;
        }

        return Task.CompletedTask;
    }

    public string ProviderName => "Claude";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; private set; } = s_fallbackModels;

    // Discovery is bounded as a whole, not just per page.
    internal TimeSpan ModelDiscoveryDeadline { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveModelId(model);
        var requestBody = new
        {
            model = modelId,
            max_tokens = LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText),
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userText } },
        };

        var json = JsonSerializer.Serialize(requestBody, s_jsonOptions);

        using var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/v1/messages", ApiKey!);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct, ErrorFormatter(modelId));
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        return ParseMessage(responseBody);
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
            throw new PluginRequestException(Loc.L("Settings.ApiKeyNotConfigured"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveModelId(model);
        var requestBody = new
        {
            model = modelId,
            max_tokens = LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText),
            stream = true,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userText } },
        };

        var json = JsonSerializer.Serialize(requestBody, s_jsonOptions);

        using var request = CreateRequest(HttpMethod.Post, $"{BaseUrl}/v1/messages", ApiKey!);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // ResponseHeadersRead so deltas surface as they arrive instead of
        // buffering the whole SSE body (the batch path reads the body to a string).
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseHeadersRead, ct, ErrorFormatter(modelId));

        await using var stream = await OpenAiApiHelper.ReadBodyWithErrorHandlingAsync(
            () => response.Content.ReadAsStreamAsync(ct), ct);
        using var reader = new StreamReader(stream);

        await foreach (var delta in SseEventDecoder.ReadValidatedAsync(reader, new ClaudeMessagesSsePolicy(), ct))
            yield return delta;
    }

    private string ResolveModelId(string? model) =>
        string.IsNullOrWhiteSpace(model) ? SupportedModels[0].Id : model.Trim();

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string apiKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        return request;
    }

    // A Messages 404 means the model isn't available to this account, e.g. a retired one.
    private Func<HttpResponseMessage, string, string> ErrorFormatter(string? modelId) =>
        (errorResponse, _) =>
        {
            _host?.Log(PluginLogLevel.Error, $"Anthropic API error {(int)errorResponse.StatusCode}: {errorResponse.ReasonPhrase}");
            return modelId is not null && errorResponse.StatusCode == HttpStatusCode.NotFound
                ? Loc.L("Errors.ModelNotFound", modelId)
                : $"Anthropic API returned {(int)errorResponse.StatusCode}: {errorResponse.ReasonPhrase}";
        };

    /// <summary>
    ///     Returns the visible answer, skipping thinking blocks. Tool use, unknown blocks and any
    ///     stop reason other than <c>end_turn</c> are rejected.
    /// </summary>
    internal static string ParseMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new PluginRequestException("Anthropic returned an empty response.", PluginRequestFailureKind.EmptyResponse);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw IncompleteMessage();
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw IncompleteMessage();

            LlmResponseTruncationGuard.ThrowIfAnthropicResponseTruncated(root, "Anthropic");
            if (!root.TryGetProperty("stop_reason", out var stopReason)
                || stopReason.ValueKind != JsonValueKind.String
                || stopReason.GetString() != "end_turn"
                || !root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                throw IncompleteMessage();
            }

            var answer = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String)
                {
                    throw IncompleteMessage();
                }

                switch (type.GetString())
                {
                    case "thinking" or "redacted_thinking":
                        continue;
                    case "text" when block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                        answer.Append(text.GetString());
                        break;
                    default:
                        throw IncompleteMessage();
                }
            }

            var result = answer.ToString().Trim();
            return result.Length > 0
                ? result
                : throw new PluginRequestException("Anthropic returned no visible answer.", PluginRequestFailureKind.EmptyResponse);
        }
    }

    private static PluginRequestException IncompleteMessage() =>
        new("Anthropic returned an invalid or incomplete message.", PluginRequestFailureKind.OutputIncomplete);

    private static string? ParseStreamStopReason(string data)
    {
        using var doc = JsonDocument.Parse(data);
        return doc.RootElement.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("stop_reason", out var reason)
            && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
    }

    private static string? ParseStreamEventType(string dataPayload)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataPayload);
            var root = doc.RootElement;
            return root.TryGetProperty("type", out var type)
                   && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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

    // One instance per stream: it tracks whether any visible text arrived.
    private sealed class ClaudeMessagesSsePolicy : ISseEventPolicy<string>
    {
        private bool _sawText;

        public string StreamName => "Anthropic stream";
        public string ExpectedTerminal => "a message_stop event";

        public SsePolicyDecision<string> Evaluate(SseEvent sseEvent)
        {
            // Also catch `event: error` frames whose payload isn't parseable
            // JSON, which ParseStreamError alone would miss.
            if (ParseStreamError(sseEvent.Data) is { } error)
            {
                return new SsePolicyDecision<string>(
                    Error: new InvalidOperationException(error));
            }

            if (sseEvent.EventType == "error")
            {
                return new SsePolicyDecision<string>(
                    Error: new InvalidOperationException("Anthropic streaming error."));
            }

            var payloadType = ParseStreamEventType(sseEvent.Data);
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- both cases would need `when` guards; the separate checks read more plainly.
            if (payloadType == "message_delta" && ParseStreamStopReason(sseEvent.Data) is { } stopReason)
            {
                if (LlmResponseTruncationGuard.IsTokenLimitReason(stopReason))
                    return new SsePolicyDecision<string>(Error: new PluginRequestException(
                        "Anthropic stopped the response at its output token limit.",
                        PluginRequestFailureKind.OutputTruncated, isTransient: false));

                // Refusal, tool use and paused turns are not a finished text answer.
                if (stopReason != "end_turn")
                    return new SsePolicyDecision<string>(Error: IncompleteMessage());
            }

            if (payloadType == "message_stop" && !_sawText)
                return new SsePolicyDecision<string>(Error: new PluginRequestException(
                    "Anthropic returned no visible answer.", PluginRequestFailureKind.EmptyResponse));

            var delta = ParseStreamDelta(sseEvent.Data);
            _sawText |= !string.IsNullOrWhiteSpace(delta);
            return new SsePolicyDecision<string>(
                HasDelta: delta is { Length: > 0 },
                Delta: delta,
                AcceptTerminal: payloadType == "message_stop");
        }
    }

    internal bool IsConfigured => !string.IsNullOrEmpty(ApiKey);
    internal string? ApiKey { get; private set; }

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
        var trimmed = apiKey.Trim();
        var normalized = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        var changed = !string.Equals(ApiKey, normalized, StringComparison.Ordinal);

        // A discovered catalog belongs to the account that fetched it. Clear it before the new
        // key takes effect, so a failed write keeps the old key and catalog together.
        if (changed)
        {
            lock (_catalogLock)
            {
                _host?.SetSetting(FetchedModelsSettingKey, new List<ClaudeModel>());
                _catalogGeneration++;
                ApiKey = normalized;
                SetCatalog([]);
            }
        }

        if (_host is not null)
        {
            if (string.IsNullOrEmpty(trimmed))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", trimmed);

            _host.NotifyCapabilitiesChanged();
        }
    }

    /// <summary>
    ///     Lists every model available to the key across <c>after_id</c> pages. An empty, malformed,
    ///     repeating or unbounded catalog is rejected whole, so a partial list is never saved.
    /// </summary>
    internal async Task<List<ClaudeModel>> FetchModelsAsync(string apiKey, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDiscoveryDeadline);
        try
        {
            return await FetchModelPagesAsync(apiKey, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "Loading Claude models timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    private async Task<List<ClaudeModel>> FetchModelPagesAsync(string apiKey, CancellationToken ct)
    {
        var models = new List<ClaudeModel>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < MaxModelPages; page++)
        {
            var uri = $"{BaseUrl}/v1/models?limit={ModelPageLimit}"
                + (cursor is null ? "" : "&after_id=" + Uri.EscapeDataString(cursor));
            using var request = CreateRequest(HttpMethod.Get, uri, apiKey);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, ct, ErrorFormatter(null));
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!TryParseModelPage(body, models, out var hasMore, out var lastId))
                throw InvalidCatalog();
            if (!hasMore)
                return models.Count > 0 ? NormalizeModels(models) : throw InvalidCatalog();
            if (lastId is null || !cursors.Add(lastId))
                throw InvalidCatalog();
            cursor = lastId;
        }

        throw InvalidCatalog();
    }

    private static bool TryParseModelPage(
        string body, List<ClaudeModel> models, out bool hasMore, out string? lastId)
    {
        hasMore = false;
        lastId = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var more)
                || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !IsModelId(id.GetString()))
                {
                    return false;
                }

                var displayName = item.TryGetProperty("display_name", out var name)
                    && name.ValueKind == JsonValueKind.String
                        ? name.GetString()
                        : null;
                models.Add(new ClaudeModel(id.GetString()!, displayName));
            }

            hasMore = more.GetBoolean();
            if (!hasMore)
                return true;

            if (data.GetArrayLength() == 0
                || !root.TryGetProperty("last_id", out var last)
                || last.ValueKind != JsonValueKind.String
                || !IsModelId(last.GetString()))
            {
                return false;
            }

            lastId = last.GetString();
            return true;
        }
    }

    private static PluginRequestException InvalidCatalog() =>
        new("Anthropic returned an empty, invalid or incomplete model catalog.",
            PluginRequestFailureKind.OutputIncomplete, isTransient: false);

    // Commits only when no key change or deactivation happened since the refresh began.
    private bool TryCommitCatalog(List<ClaudeModel> models, long generation, string apiKey)
    {
        IPluginHostServices host;
        lock (_catalogLock)
        {
            if (_host is null
                || generation != _catalogGeneration
                || !string.Equals(ApiKey, apiKey, StringComparison.Ordinal))
            {
                return false;
            }

            host = _host;
            // Persist first so a failed write leaves the live catalog untouched.
            host.SetSetting(FetchedModelsSettingKey, models);
            SetCatalog(models);
        }

        host.NotifyCapabilitiesChanged();
        return true;
    }

    // Caller holds _catalogLock. The upstream default leads whenever the account offers it;
    // otherwise the account's own (newest-first) order is kept.
    private void SetCatalog(List<ClaudeModel> models)
    {
        SupportedModels = models.Count == 0
            ? s_fallbackModels
            : models
                .OrderBy(m => m.Id == DefaultModelId ? 0 : 1)
                .Select(m => new PluginModelInfo(m.Id, m.DisplayName ?? KnownDisplayName(m.Id) ?? m.Id))
                .ToList();
    }

    private static string? KnownDisplayName(string id) =>
        s_fallbackModels.FirstOrDefault(m => m.Id == id)?.DisplayName;

    private static List<ClaudeModel> NormalizeModels(IEnumerable<ClaudeModel> models) =>
        models
            .Where(m => IsModelId(m.Id))
            .DistinctBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m with { DisplayName = IsDisplayName(m.DisplayName) ? m.DisplayName!.Trim() : null })
            .ToList();

    private static bool IsModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxModelIdLength
        && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool IsDisplayName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxModelIdLength && !name.Any(char.IsControl);

    internal static bool ValidateApiKeyFormat(string apiKey)
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
                "api-key" => ApiKey,
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

    // Validation doubles as the model refresh; any failure leaves the saved catalog untouched.
    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        if (!ValidateApiKeyFormat(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyFormatInvalid"));

        long generation;
        lock (_catalogLock)
            generation = _catalogGeneration;

        List<ClaudeModel> models;
        try
        {
            models = await FetchModelsAsync(apiKey, ct);
        }
        catch (PluginRequestException ex) when (ex.FailureKind is PluginRequestFailureKind.Authentication
                                                    or PluginRequestFailureKind.Permission)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyRejected"));
        }
        catch (PluginRequestException ex)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshFailed", ex.Message));
        }

        return TryCommitCatalog(models, generation, apiKey)
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValidFetchedModels", models.Count))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ModelRefreshStale"));
    }
}

internal sealed record ClaudeModel(string Id, string? DisplayName);
