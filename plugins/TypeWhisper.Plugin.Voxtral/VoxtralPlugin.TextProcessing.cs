using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Voxtral;

// Mistral chat completions for text processing, sharing the Voxtral API key.
public sealed partial class VoxtralPlugin
{
    private const string TextModelsSettingKey = "fetchedTextModels";
    private const string TemperatureModeSettingKey = "temperatureMode";
    private const string TemperatureSettingKey = "temperature";
    private const string ProviderDefaultTemperatureMode = "provider-default";
    private const string CustomTemperatureMode = "custom";
    private const double DefaultCustomTemperature = 0.3;

    private const int MaxCatalogModels = 1000;
    private const int MaxModelIdLength = 256;

    private const string DefaultTextModelId = "mistral-small-latest";

    private static readonly IReadOnlyList<PluginModelInfo> s_fallbackTextModels =
    [
        new(DefaultTextModelId, "Mistral Small"),
        new("mistral-medium-latest", "Mistral Medium"),
        new("mistral-large-latest", "Mistral Large"),
    ];

    private static readonly Dictionary<string, string> s_knownTextDisplayNames = s_fallbackTextModels
        .ToDictionary(m => m.Id, m => m.DisplayName, StringComparer.Ordinal);

    private static readonly Dictionary<string, int> s_knownTextModelRanks = s_fallbackTextModels
        .Select((m, rank) => (m.Id, rank))
        .ToDictionary(m => m.Id, m => m.rank, StringComparer.Ordinal);

    private readonly Lock _catalogLock = new();
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private bool _streamResponses = true;
    private string _temperatureMode = ProviderDefaultTemperatureMode;
    private double _temperature = DefaultCustomTemperature;

    // Bumped on key change or deactivation so an in-flight refresh cannot commit.
    private long _catalogGeneration;

    public string ProviderName => "Mistral";
    public bool IsAvailable => IsConfigured;

    public IReadOnlyList<PluginModelInfo> SupportedModels { get; private set; } = s_fallbackTextModels;

    // Discovery is bounded as a whole, not only by the HTTP client timeout.
    internal TimeSpan ModelDiscoveryDeadline { get; init; } = TimeSpan.FromSeconds(60);

    private void ActivateTextRole(IPluginHostServices host)
    {
        _streamResponses = host.GetSetting<bool?>(LlmStreamingSettings.StreamResponsesSettingKey) ?? true;
        _temperatureMode = NormalizeTemperatureMode(host.GetSetting<string>(TemperatureModeSettingKey));
        _temperature = host.GetSetting<double?>(TemperatureSettingKey) is { } saved && IsTemperature(saved)
            ? saved
            : DefaultCustomTemperature;
        lock (_catalogLock)
            SetTextCatalog(LoadSavedTextCatalog(host, ApiKey));
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveTextModelId(model);
        return await OpenAiChatHelper.SendChatCompletionAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateChatRequestOptions(modelId, systemPrompt, userText),
            ct
        );
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

        if (!IsAvailable)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredMistralApiKeyRequired"), PluginRequestFailureKind.Configuration);

        var modelId = ResolveTextModelId(model);
        var source = OpenAiChatHelper.SendChatCompletionStreamingAsync(
            _httpClient,
            BaseUrl,
            ApiKey!,
            modelId,
            systemPrompt,
            userText,
            CreateChatRequestOptions(modelId, systemPrompt, userText),
            ct
        );

        await foreach (var delta in source)
            yield return delta;
    }

    private string ResolveTextModelId(string? model) =>
        string.IsNullOrWhiteSpace(model) ? SupportedModels[0].Id : model.Trim();

    // Mistral rejects prompt + max_tokens beyond the context, so only Magistral gets the reasoning reserve.
    private OpenAiChatRequestOptions CreateChatRequestOptions(string modelId, string systemPrompt, string userText) =>
        new()
        {
            ProviderName = ProviderName,
            MaxOutputTokens = modelId.StartsWith("magistral-", StringComparison.Ordinal)
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve(systemPrompt, userText)
                : LlmOutputTokenBudget.Calculate(systemPrompt, userText),
            ScaleOutputTokens = false,
            Temperature = _temperatureMode == CustomTemperatureMode ? _temperature : null,
        };

    // A catalog saved for a different key is ignored.
    private static List<string> LoadSavedTextCatalog(IPluginHostServices host, string? apiKey) =>
        host.GetSetting<MistralTextModelCatalog>(TextModelsSettingKey) is { } saved && saved.Owner == Fingerprint(apiKey)
            ? NormalizeModels(saved.Models ?? [])
            : [];

    internal static string Fingerprint(string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"TypeWhisper.Mistral.textCatalog/1\n{apiKey}")));

    /// <summary>
    ///     Lists the chat-completion models available to the key. An empty, malformed or oversized
    ///     catalog is rejected whole, so a partial list is never saved.
    /// </summary>
    internal async Task<List<string>> FetchTextModelsAsync(string apiKey, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ModelDiscoveryDeadline);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, deadline.Token,
                (errorResponse, errorBody) => $"Mistral API error {(int)errorResponse.StatusCode}: {errorBody}");
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            return ParseTextModelCatalog(body) ?? throw InvalidTextCatalog();
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                Loc.L("Errors.ModelRefreshTimedOut"), PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    // Transcription, embedding, OCR and moderation models lack completion_chat.
    private static List<string>? ParseTextModelCatalog(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() is 0 or > MaxCatalogModels)
            {
                return null;
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !IsModelId(id.GetString())
                    || !item.TryGetProperty("capabilities", out var capabilities)
                    || capabilities.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (IsTrue(capabilities, "completion_chat") && !IsTrue(item, "archived"))
                    ids.Add(id.GetString()!);
            }

            var models = NormalizeModels(ids);
            return models.Count == 0 ? null : models;
        }
    }

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private PluginRequestException InvalidTextCatalog() =>
        new(Loc.L("Errors.ModelCatalogInvalid"),
            PluginRequestFailureKind.OutputIncomplete, isTransient: false);

    // Commits only when no key change or deactivation happened since the refresh began.
    private bool TryCommitTextCatalog(List<string> models, long generation, string apiKey)
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
            host.SetSetting(TextModelsSettingKey, new MistralTextModelCatalog(Fingerprint(apiKey), models));
            SetTextCatalog(models);
        }

        host.NotifyCapabilitiesChanged();
        return true;
    }

    // Caller holds _catalogLock. Known general models lead, default first, for blank-model requests.
    private void SetTextCatalog(List<string> models)
    {
        SupportedModels = models.Count == 0
            ? s_fallbackTextModels
            : models
                .OrderBy(id => s_knownTextModelRanks.GetValueOrDefault(id, s_knownTextModelRanks.Count))
                .ThenBy(id => id, StringComparer.Ordinal)
                .Select(id => new PluginModelInfo(id, s_knownTextDisplayNames.GetValueOrDefault(id, id)))
                .ToList();
    }

    private static List<string> NormalizeModels(IEnumerable<string?> models) =>
        models
            .Where(IsModelId)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool IsModelId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxModelIdLength
        && !id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private void SetTemperatureMode(string? value)
    {
        var mode = NormalizeTemperatureMode(value);
        _host?.SetSetting(TemperatureModeSettingKey, mode);
        _temperatureMode = mode;
    }

    private void SetTemperature(string? value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
            || !IsTemperature(temperature))
        {
            throw new ArgumentException(Loc.L("Settings.TemperatureInvalid"));
        }

        _host?.SetSetting(TemperatureSettingKey, temperature);
        _temperature = temperature;
    }

    private static string NormalizeTemperatureMode(string? value) =>
        value == CustomTemperatureMode ? CustomTemperatureMode : ProviderDefaultTemperatureMode;

    // Mistral's chat-completions schema caps temperature at 1.5.
    private static bool IsTemperature(double value) => double.IsFinite(value) && value is >= 0 and <= 1.5;

    private void SetStreamResponses(bool enabled)
    {
        _host?.SetSetting(LlmStreamingSettings.StreamResponsesSettingKey, enabled);
        _streamResponses = enabled;
    }

    private static bool ParseBool(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

internal sealed record MistralTextModelCatalog(string? Owner, List<string>? Models);
