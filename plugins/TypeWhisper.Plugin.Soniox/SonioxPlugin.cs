// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Soniox;

public sealed class SonioxPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    internal const string DefaultModelId = "default";

    internal const string DefaultRegionId = "us";
    private const string RegionSettingKey = "region";
    private const string ApiKeySecretName = "api-key";
    private const string SonioxAsyncModelId = "stt-async-v5";
    private const int DefaultMaxPollAttempts = 3600;
    private const int MaxSubtitleSegmentCharacters = 84;
    private const int MinSentenceSegmentCharacters = 20;
    private const double MaxSubtitleSegmentDurationSeconds = 6.0;
    private const double SubtitleSegmentPauseSplitSeconds = 0.75;

    // Short first delay so brief clips return fast; back off toward a cap so long
    // recordings do not hammer the API.
    private static readonly TimeSpan s_defaultInitialPollDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan s_defaultMaxPollDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_defaultCleanupBudget = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        new(DefaultModelId, "Soniox Async")
        {
            IsRecommended = true,
        },
    ];

    // Keys are issued per regional project and both REST and realtime hosts are regional.
    // https://soniox.com/docs/stt/data-residency
    internal static IReadOnlyList<SonioxRegion> AvailableRegions { get; } =
    [
        new(DefaultRegionId, "Settings.RegionUs", "https://api.soniox.com", "wss://stt-rt.soniox.com/transcribe-websocket"),
        new("eu", "Settings.RegionEu", "https://api.eu.soniox.com", "wss://stt-rt.eu.soniox.com/transcribe-websocket"),
        new("jp", "Settings.RegionJp", "https://api.jp.soniox.com", "wss://stt-rt.jp.soniox.com/transcribe-websocket"),
        new("in", "Settings.RegionIn", "https://api.in.soniox.com", "wss://stt-rt.in.soniox.com/transcribe-websocket"),
    ];

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _initialPollDelay;
    private readonly TimeSpan _maxPollDelay;
    private readonly int _maxPollAttempts;
    private readonly TimeSpan _cleanupBudget;
    private readonly TimeSpan _regionProbeTimeout;
    private readonly SemaphoreSlim _apiKeyWriteLock = new(1, 1);
    private readonly Lock _cleanupChainLock = new();
    private readonly Lock _regionLock = new();

    private IPluginHostServices? _host;
    private string _selectedModelId = DefaultModelId;

    public SonioxPlugin()
        : this(CreateHttpClient())
    {
    }

    internal SonioxPlugin(
        HttpClient httpClient,
        TimeSpan? pollDelay = null,
        int maxPollAttempts = DefaultMaxPollAttempts,
        TimeSpan? cleanupBudget = null,
        TimeSpan? regionProbeTimeout = null)
    {
        if (maxPollAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPollAttempts), "Poll attempts must be positive.");

        var resolvedCleanupBudget = cleanupBudget ?? s_defaultCleanupBudget;
        if (resolvedCleanupBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupBudget), "Cleanup budget must be positive.");

        var resolvedRegionProbeTimeout = regionProbeTimeout ?? TimeSpan.FromSeconds(10);
        if (resolvedRegionProbeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(regionProbeTimeout), "Region probe timeout must be positive.");

        _regionProbeTimeout = resolvedRegionProbeTimeout;
        _httpClient = httpClient;
        _initialPollDelay = pollDelay ?? s_defaultInitialPollDelay;
        _maxPollDelay = pollDelay ?? s_defaultMaxPollDelay;
        _maxPollAttempts = maxPollAttempts;
        _cleanupBudget = resolvedCleanupBudget;
    }

    // ITypeWhisperPlugin

    public string PluginId => "com.typewhisper.soniox";
    public string PluginName => "Soniox";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = NormalizeApiKey(await host.LoadSecretAsync(ApiKeySecretName));
        _selectedModelId = DefaultModelId;
        RegionId = NormalizeRegionId(host.GetSetting<string>(RegionSettingKey));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured}, region={RegionId})");
    }

    public async Task DeactivateAsync()
    {
        // Uploaded audio must not outlive the session: finish pending deletions before the host
        // (and then the HttpClient) go away. CleanupAsync already bounds each deletion. A
        // transcription finishing mid-drain appends to the chain, so loop until it stops growing.
        Task pending;
        do
        {
            lock (_cleanupChainLock)
            {
                pending = LastCleanupTask;
            }

            await pending;
        } while (!IsCleanupChainAt(pending));

        _host = null;
    }

    // ITranscriptionEnginePlugin

    public string ProviderId => "soniox";
    public string ProviderDisplayName => "Soniox";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the interface contract, which declares this member nullable.
    public string? SelectedModelId => _selectedModelId;

    public bool SupportsTranslation => false;

    public bool SupportsStreaming => true;
    public bool SupportsLanguageHints => true;
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;

    public async Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        => await StartStreamingWithLanguageHintsAsync(
            NormalizeLanguage(language) is { } l ? [l] : [], ct);

    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        return await ConnectStreaming(ApiKey!, RealtimeUri, languageHints, ct);
    }

    public void SelectModel(string modelId)
    {
        if (!string.Equals(modelId, DefaultModelId, StringComparison.Ordinal))
            throw new ArgumentException($"Unknown model: {modelId}");

        _selectedModelId = DefaultModelId;
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Soniox does not support translation.");

        return await TranscribeCoreAsync(
            wavAudio, NormalizeLanguage(language) is { } l ? [l] : [], fallbackLanguage: NormalizeLanguage(language), ct);
    }

    // Soniox has no progress callback, so the polling preview is the batch call with every hint.
    public Task<PluginTranscriptionResult> TranscribeStreamingWithLanguageHintsAsync(
        byte[] wavAudio, IReadOnlyList<string> languageHints, bool translate, string? prompt,
        Func<string, bool> onProgress, CancellationToken ct) =>
        TranscribeWithLanguageHintsAsync(wavAudio, languageHints, translate, prompt, ct);

    // Hints are advisory, so none of them is claimed as the detected language when the
    // transcript itself carries no language; only an explicit single language is.
    public async Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
        byte[] wavAudio, IReadOnlyList<string> languageHints, bool translate, string? prompt,
        CancellationToken ct)
    {
        if (translate)
            throw new InvalidOperationException("Soniox does not support translation.");

        return await TranscribeCoreAsync(wavAudio, languageHints, fallbackLanguage: null, ct);
    }

    private async Task<PluginTranscriptionResult> TranscribeCoreAsync(
        byte[] wavAudio, IReadOnlyList<string> languageHints, string? fallbackLanguage, CancellationToken ct)
    {

        // Snapshot the key and region once so a concurrent settings change can't swap the key
        // or the region out partway through the multi-request async flow below.
        var apiKey = ApiKey;
        var region = Region;
        if (string.IsNullOrEmpty(apiKey))
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        string? fileId = null;
        string? transcriptionId = null;

        try
        {
            fileId = await UploadFileAsync(wavAudio, region.BaseUrl, apiKey, ct);
            var normalizedLanguageHints = languageHints
                .Select(NormalizeLanguage)
                .Where(static language => language is not null)
                .Select(static language => language!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            transcriptionId = await CreateTranscriptionAsync(fileId, normalizedLanguageHints, region.BaseUrl, apiKey, ct);
            var completedDetails = await WaitUntilCompletedAsync(transcriptionId, region.BaseUrl, apiKey, ct);
            var transcriptJson = await FetchTranscriptAsync(transcriptionId, region.BaseUrl, apiKey, ct);
            var result = ParseTranscript(transcriptJson, completedDetails, fallbackLanguage);
            // Chain rather than replace so an overlapping transcription's cleanup is never lost
            // and DeactivateAsync can drain every pending deletion.
            var cleanup = CleanupInBackgroundAsync(transcriptionId, fileId, region.BaseUrl, apiKey);
            lock (_cleanupChainLock)
            {
                LastCleanupTask = Task.WhenAll(LastCleanupTask, cleanup);
            }

            return result;
        }
        catch
        {
            await CleanupAsync(transcriptionId, fileId, region.BaseUrl, apiKey);
            throw;
        }
    }

    // IPluginSettingsProvider

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                IsSecret: true,
                Description: Loc.L("Settings.ApiKeyDescription")),
            new(
                RegionSettingKey,
                Loc.L("Settings.Region"),
                Description: Loc.L("Settings.RegionDescription"),
                Options: AvailableRegions.Select(r => new PluginSettingOption(r.Id, Loc.L(r.LabelKey))).ToList(),
                Kind: PluginSettingKind.Dropdown),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                RegionSettingKey => RegionId,
                _ => null,
            });

    public async Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        switch (key)
        {
            case "api-key":
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case RegionSettingKey:
                SetRegion(value);
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyRequired"));

        var probedKey = ApiKey;
        var probedRegionId = RegionId;
        var detected = await DetectRegionAsync(probedKey, ct);
        if (detected is null)
            return new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));

        var detectedLabel = Loc.L(ResolveRegion(detected).LabelKey);
        lock (_regionLock)
        {
            // The probes can take a while; if the key or region was saved again meanwhile, the
            // result belongs to the old settings and must neither be reported nor persisted.
            if (!string.Equals(ApiKey, probedKey, StringComparison.Ordinal) || RegionId != probedRegionId)
                return new PluginSettingsValidationResult(false, Loc.L("Settings.SettingsChangedDuringProbe"));

            if (detected == probedRegionId)
                return new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValidRegion", detectedLabel));

            SetRegionLocked(detected);
        }

        return new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValidRegionSwitched", detectedLabel));
    }

    // Settings support

    internal string? ApiKey { get; private set; }

    /// <summary>Tests await this task to observe background cleanup deterministically.</summary>
    internal Task LastCleanupTask { get; private set; } = Task.CompletedTask;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal sealed record SonioxRegion(string Id, string LabelKey, string BaseUrl, string RealtimeUrl);

    internal string RegionId { get; private set; } = DefaultRegionId;
    internal SonioxRegion Region => ResolveRegion(RegionId);
    internal Uri RealtimeUri => new(Region.RealtimeUrl);

    private static SonioxRegion ResolveRegion(string? id) =>
        AvailableRegions.FirstOrDefault(region =>
            string.Equals(region.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? AvailableRegions[0];

    private static string NormalizeRegionId(string? id) => ResolveRegion(id).Id;

    internal void SetRegion(string? regionId)
    {
        lock (_regionLock)
        {
            SetRegionLocked(regionId);
        }
    }

    private void SetRegionLocked(string? regionId)
    {
        var normalized = NormalizeRegionId(regionId);
        if (RegionId == normalized)
            return;

        // Persist first so a failing settings store leaves the live region untouched and the
        // same value can be saved again.
        _host?.SetSetting(RegionSettingKey, normalized);
        RegionId = normalized;
    }

    /// <summary>Opens the realtime session; tests replace it to observe the endpoint the plugin picks.</summary>
    internal Func<string, Uri, IReadOnlyList<string>, CancellationToken, Task<IStreamingSession>> ConnectStreaming { get; set; } =
        static async (apiKey, realtimeUri, languageHints, ct) =>
            await SonioxStreamingSession.ConnectAsync(apiKey, realtimeUri, languageHints, ct);

    internal async Task<string?> DetectRegionAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return null;

        var selected = Region;
        // The selected region answers in one request normally; a key only works in its own
        // region, so the fallback sweep is a correction, not a guess.
        foreach (var region in AvailableRegions.Where(r => r.Id != selected.Id).Prepend(selected))
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(_regionProbeTimeout);
            try
            {
                if (await ProbeModelsAsync(region.BaseUrl, normalized, probeCts.Token))
                    return region.Id;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // This region's probe timed out; the next one may still answer.
            }
        }

        return null;
    }

    internal async Task SetApiKeyAsync(string apiKey)
    {
        var normalized = NormalizeApiKey(apiKey);
        IPluginHostServices? hostToNotify = null;

        await _apiKeyWriteLock.WaitAsync();
        try
        {
            var wasConfigured = IsConfigured;
            var changed = !string.Equals(ApiKey, normalized, StringComparison.Ordinal);

            if (!changed)
                return;

            if (_host is not null)
            {
                if (normalized is null)
                    await _host.DeleteSecretAsync(ApiKeySecretName);
                else
                    await _host.StoreSecretAsync(ApiKeySecretName, normalized);

                hostToNotify = _host;
            }

            // Update in-memory state after the persistence call succeeds so a
            // failing secret store leaves the live key untouched.
            ApiKey = normalized;

            if (wasConfigured == IsConfigured)
                hostToNotify = null;
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        hostToNotify?.NotifyCapabilitiesChanged();
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var normalized = NormalizeApiKey(apiKey);
        if (normalized is null)
            return false;

        return await ProbeModelsAsync(Region.BaseUrl, normalized, ct);
    }

    private async Task<bool> ProbeModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
            AddAuthorization(request, apiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<string> UploadFileAsync(byte[] wavAudio, string baseUrl, string apiKey, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/files");
        AddAuthorization(request, apiKey);
        request.Content = form;

        var json = await SendJsonAsync(request, "Soniox file upload", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox file upload response did not include a file id.");
    }

    private async Task<string> CreateTranscriptionAsync(
        string fileId,
        List<string> languageHints,
        string baseUrl,
        string apiKey,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = SonioxAsyncModelId,
            ["file_id"] = fileId,
        };

        if (languageHints.Count > 0)
            payload["language_hints"] = languageHints;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/transcriptions");
        AddAuthorization(request, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var json = await SendJsonAsync(request, "Soniox transcription creation", ct);
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "id")
            ?? throw new InvalidOperationException("Soniox transcription response did not include a transcription id.");
    }

    private async Task<JsonElement> WaitUntilCompletedAsync(string transcriptionId, string baseUrl, string apiKey, CancellationToken ct)
    {
        var delay = _initialPollDelay;
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/transcriptions/{transcriptionId}");
            AddAuthorization(request, apiKey);

            var json = await SendJsonAsync(request, "Soniox transcription status", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = GetString(root, "status");

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return root.Clone();

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Soniox transcription failed: {ExtractApiError(root)}");

            if (attempt >= _maxPollAttempts - 1 || delay <= TimeSpan.Zero)
                continue;

            await Task.Delay(delay, ct);
            delay = NextPollDelay(delay, _maxPollDelay);
        }

        throw new TimeoutException(
            $"Soniox transcription {transcriptionId} did not complete within the configured polling window.");
    }

    internal static TimeSpan NextPollDelay(TimeSpan current, TimeSpan max) =>
        TimeSpan.FromTicks((long)Math.Min(current.Ticks * 1.5, max.Ticks));

    private async Task<string> FetchTranscriptAsync(string transcriptionId, string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/v1/transcriptions/{transcriptionId}/transcript");
        AddAuthorization(request, apiKey);

        return await SendJsonAsync(request, "Soniox transcript retrieval", ct);
    }

    private async Task<string> SendJsonAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} error {(int)response.StatusCode}: {ExtractApiError(json)}");
        }

        return json;
    }

    private bool IsCleanupChainAt(Task task)
    {
        lock (_cleanupChainLock)
        {
            return ReferenceEquals(LastCleanupTask, task);
        }
    }

    private async Task CleanupInBackgroundAsync(string? transcriptionId, string? fileId, string baseUrl, string apiKey)
    {
        await Task.Yield();

        try
        {
            await CleanupAsync(transcriptionId, fileId, baseUrl, apiKey);
        }
        catch (Exception ex)
        {
            _host?.Log(PluginLogLevel.Warning, $"Soniox background cleanup failed: {ex.Message}");
        }
    }

    private async Task CleanupAsync(string? transcriptionId, string? fileId, string baseUrl, string apiKey)
    {
        using var cleanupCts = new CancellationTokenSource(_cleanupBudget);
        var cleanupToken = cleanupCts.Token;

        if (transcriptionId is not null)
        {
            var transcriptionDeleted = await DeleteBestEffortAsync(
                $"{baseUrl}/v1/transcriptions/{transcriptionId}",
                "transcription",
                apiKey,
                cleanupToken);
            if (transcriptionDeleted)
                return;
        }

        if (fileId is not null && !cleanupToken.IsCancellationRequested)
        {
            await DeleteBestEffortAsync(
                $"{baseUrl}/v1/files/{fileId}",
                "file",
                apiKey,
                cleanupToken);
        }
    }

    private async Task<bool> DeleteBestEffortAsync(
        string uri,
        string resourceName,
        string apiKey,
        CancellationToken cleanupToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        AddAuthorization(request, apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cleanupToken);
            if (response.IsSuccessStatusCode)
                return true;

            var json = await response.Content.ReadAsStringAsync(cleanupToken);
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox cleanup could not delete {resourceName}: {(int)response.StatusCode} {ExtractApiError(json)}");
        }
        catch (HttpRequestException ex)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox cleanup could not delete {resourceName} because the HTTP request failed: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox cleanup timed out while deleting {resourceName}: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            var reason = cleanupToken.IsCancellationRequested
                ? "the cleanup budget expired"
                : $"the request was canceled: {ex.Message}";
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox cleanup could not delete {resourceName} because {reason}.");
        }
        catch (Exception ex)
        {
            _host?.Log(
                PluginLogLevel.Warning,
                $"Soniox cleanup could not delete {resourceName} because an unexpected error occurred: {ex.Message}");
        }

        return false;
    }

    internal static PluginTranscriptionResult ParseTranscript(
        string transcriptJson,
        JsonElement completedDetails,
        string? fallbackLanguage)
    {
        using var doc = JsonDocument.Parse(transcriptJson);
        var root = doc.RootElement;
        var text = GetString(root, "text")?.Trim() ?? "";
        var duration = TryGetDouble(completedDetails, "audio_duration_ms", out var durationMs)
            ? durationMs / 1000.0
            : 0.0;

        var segmentTokens = new List<SonioxTimedToken>();
        string? detectedLanguage = null;
        var transcriptCursor = 0;

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("tokens", out var tokens)
            && tokens.ValueKind == JsonValueKind.Array)
        {
            foreach (var token in tokens.EnumerateArray())
            {
                var tokenText = GetString(token, "text");
                if (string.IsNullOrWhiteSpace(tokenText))
                    continue;

                detectedLanguage ??= GetString(token, "language");

                if (!TryGetDouble(token, "start_ms", out var startMs)
                    || !TryGetDouble(token, "end_ms", out var endMs))
                {
                    continue;
                }

                var start = startMs / 1000.0;
                var end = endMs / 1000.0;
                var displayText = ResolveDisplayText(text, tokenText, ref transcriptCursor);

                // Drop tokens with a non-positive duration — Soniox occasionally emits
                // zero/inverted ranges that would otherwise corrupt subtitle timing.
                if (end <= start)
                    continue;

                if (!string.IsNullOrWhiteSpace(displayText))
                    segmentTokens.Add(new SonioxTimedToken(displayText, start, end));

                duration = Math.Max(duration, end);
            }
        }

        return new PluginTranscriptionResult(text, detectedLanguage ?? fallbackLanguage, duration, NoSpeechProbability: null)
        {
            Segments = BuildSubtitleSegments(segmentTokens),
        };
    }

    // Groups word-level Soniox tokens into subtitle-sized segments: breaks on a
    // pause, at a sentence terminator (once long enough), or when a segment grows
    // past the character/duration caps. Without this each token would become its
    // own one-word subtitle cue.
    private static List<PluginTranscriptionSegment> BuildSubtitleSegments(IReadOnlyList<SonioxTimedToken> tokens)
    {
        var segments = new List<PluginTranscriptionSegment>();
        var text = new StringBuilder();
        var start = 0.0;
        var end = 0.0;
        var hasSegment = false;

        foreach (var token in tokens)
        {
            if (hasSegment && ShouldStartNewSubtitleSegment(token, text, start, end))
                FlushSegment();

            if (!hasSegment)
            {
                text.Clear();
                start = token.Start;
                hasSegment = true;
            }

            text.Append(token.Text);
            end = token.End;

            if (ShouldEndSubtitleSegment(text, start, end))
                FlushSegment();
        }

        FlushSegment();
        return segments;

        void FlushSegment()
        {
            if (!hasSegment)
                return;

            var normalizedText = NormalizeSubtitleText(text.ToString());
            if (normalizedText.Length > 0)
                segments.Add(new PluginTranscriptionSegment(normalizedText, start, end));

            text.Clear();
            hasSegment = false;
        }
    }

    private static bool ShouldStartNewSubtitleSegment(
        SonioxTimedToken token,
        StringBuilder currentText,
        double currentStart,
        double currentEnd)
    {
        if (token.Start - currentEnd > SubtitleSegmentPauseSplitSeconds)
            return true;

        if (token.End - currentStart > MaxSubtitleSegmentDurationSeconds)
            return true;

        var combinedNormalizedLength = NormalizeSubtitleText(currentText + token.Text).Length;
        return combinedNormalizedLength > MaxSubtitleSegmentCharacters;
    }

    private static bool ShouldEndSubtitleSegment(StringBuilder currentText, double start, double end)
    {
        var normalizedText = NormalizeSubtitleText(currentText.ToString());
        if (normalizedText.Length >= MinSentenceSegmentCharacters
            && EndsWithSentenceTerminator(normalizedText))
        {
            return true;
        }

        return end - start >= MaxSubtitleSegmentDurationSeconds;
    }

    // Slices the spacing/casing from the full transcript text so grouped segments
    // keep the original spacing between tokens; falls back to the trimmed token.
    private static string ResolveDisplayText(string transcriptText, string tokenText, ref int transcriptCursor)
    {
        var trimmedToken = tokenText.Trim();
        if (trimmedToken.Length == 0)
            return "";

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (transcriptText.Length > 0 && transcriptCursor <= transcriptText.Length)
        {
            var match = transcriptText.IndexOf(trimmedToken, transcriptCursor, StringComparison.Ordinal);
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (match >= 0)
            {
                var end = match + trimmedToken.Length;
                var displayText = transcriptText[transcriptCursor..end];
                transcriptCursor = end;
                return displayText;
            }
        }

        return trimmedToken;
    }

    private static string NormalizeSubtitleText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return "";

        var sb = new StringBuilder(trimmed.Length);
        var previousWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasWhitespace)
                    sb.Append(' ');

                previousWasWhitespace = true;
                continue;
            }

            sb.Append(ch);
            previousWasWhitespace = false;
        }

        return sb.ToString();
    }

    private static bool EndsWithSentenceTerminator(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch is '"' or '\'' or ')' or ']' or '}')
                continue;

            return ch is '.' or '!' or '?';
        }

        return false;
    }

    private static void AddAuthorization(HttpRequestMessage request, string apiKey) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    private static string ExtractApiError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractApiError(doc.RootElement);
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
        }
    }

    private static string ExtractApiError(JsonElement root)
    {
        var errorType = GetString(root, "error_type");
        var message = GetString(root, "error_message")
            ?? GetString(root, "message")
            ?? GetNestedErrorMessage(root)
            ?? "Unknown error";
        var requestId = GetString(root, "request_id");

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(errorType))
            sb.Append(errorType).Append(": ");

        sb.Append(message);

        if (!string.IsNullOrWhiteSpace(requestId))
            sb.Append(" (request_id: ").Append(requestId).Append(')');

        return sb.ToString();
    }

    private static string? GetNestedErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return null;

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString(),
            JsonValueKind.Object => GetString(error, "message") ?? GetString(error, "detail"),
            _ => null,
        };
    }

    private static string? NormalizeApiKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

    private static string? NormalizeLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetDouble(JsonElement element, string propertyName, out double value)
    {
        if (element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(5) };

    private sealed record SonioxTimedToken(string Text, double Start, double End);

    public void Dispose()
    {
        _httpClient.Dispose();
        _apiKeyWriteLock.Dispose();
    }
}
