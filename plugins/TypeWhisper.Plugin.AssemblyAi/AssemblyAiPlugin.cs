// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed class AssemblyAiPlugin
    : ITranscriptionEnginePlugin,
        ITranscriptionLanguageSelectionCapabilities,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string BaseUrl = "https://api.assemblyai.com";
    private const string SyncUrl = "https://sync.assemblyai.com/v1/transcribe";
    // Off by default: keyterms cost extra on recorded audio and Universal-Streaming English.
    internal const string UseDictionaryTermsSettingName = "useDictionaryTerms";
    // Off by default: labels cost extra, and live text cannot carry them.
    internal const string SpeakerLabelsSettingName = "speakerLabels";
    internal const string TranscriptionModeSettingName = "transcriptionMode";

    // Streaming counts terms, not words; it ignores longer keyterms and rejects more than 100.
    private static readonly DictionaryTermsBudget s_streamingBudget =
        new(MaxTerms: 100, MaxCharsPerTerm: 50, MaxWordsPerTerm: 6);
    // Streaming keyterms go in the WebSocket URL; this keeps it inside common request-line limits.
    internal const int MaxStreamingKeytermsQueryLength = 6000;

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;

    private static readonly IReadOnlyList<PluginModelInfo> s_models =
    [
        .. AssemblyAiModels.All.Select(model => new PluginModelInfo(model.Id, model.DisplayName)
        {
            LanguageCount = model.Languages.Count,
            IsRecommended = model.IsPro,
        }),
    ];

    public AssemblyAiPlugin()
        : this(new HttpClient()) { }

    internal AssemblyAiPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string PluginId => "com.typewhisper.assemblyai";
    public string PluginName => "AssemblyAI";
    public string PluginVersion => PluginBuildInfo.Version;

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        ApiKey = await host.LoadSecretAsync("api-key");
        var savedModelId = host.GetSetting<string>("selectedModel");
        SelectedModelId = AssemblyAiModels.Resolve(savedModelId).Id;
        if (savedModelId is not null && savedModelId != SelectedModelId)
            host.SetSetting("selectedModel", SelectedModelId);
        UseDictionaryTerms = host.GetSetting<bool?>(UseDictionaryTermsSettingName) ?? false;
        SpeakerLabels = host.GetSetting<bool?>(SpeakerLabelsSettingName) ?? false;
        Mode = AssemblyAiModes.Normalize(host.GetSetting<string>(TranscriptionModeSettingName));
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "assemblyai";
    public string ProviderDisplayName => "AssemblyAI";
    public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels => s_models;

    public string? SelectedModelId { get; private set; } = AssemblyAiModels.DefaultId;

    public bool SupportsTranslation => false;
    // Only Live streams, so Sync and Async never bill a live connection on top of the recording.
    public bool SupportsStreaming => Mode == AssemblyAiModes.Live && !SpeakerLabels;
    public bool SupportsStructuredDictionaryTerms => UseDictionaryTerms;
    public DictionaryTermsBudget DictionaryTermsBudget => BudgetFor(SelectedModelId);
    public LanguageSelectionSupport AutomaticDetectionSupport => LanguageSelectionSupport.Supported;
    public LanguageSelectionSupport ExplicitSelectionSupport => LanguageSelectionSupport.Supported;
    public IReadOnlyList<string> SupportedLanguages => AssemblyAiModels.Resolve(SelectedModelId).Languages;

    public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
        StartStreamingWithLanguageHintsAndPromptAsync(language is null ? [] : [language], null, ct);

    public Task<IStreamingSession> StartStreamingWithLanguageHintsAsync(
        IReadOnlyList<string> languageHints,
        CancellationToken ct
    ) =>
        StartStreamingWithLanguageHintsAndPromptAsync(languageHints, null, ct);

    public async Task<IStreamingSession> StartStreamingWithLanguageHintsAndPromptAsync(
        IReadOnlyList<string> languageHints,
        string? prompt,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);
        var model = AssemblyAiModels.Resolve(SelectedModelId);
        var language = AssemblyAiModels.NormalizeLanguage(
            languageHints.FirstOrDefault(static hint => !string.IsNullOrWhiteSpace(hint)));
        // The host transcribes the recording instead when a session cannot start.
        if (!AssemblyAiModels.SupportsStreamingLanguage(model, language))
            throw new NotSupportedException("Universal-2 real-time dictation needs one of its six streaming languages selected.");
        return await AssemblyAiStreamingSession.ConnectAsync(
            ApiKey!,
            model.Id,
            language,
            StreamingKeytermsFor(prompt, UseDictionaryTerms),
            ct
        );
    }

    public void SelectModel(string modelId)
    {
        var model = AssemblyAiModels.Find(modelId) ?? throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = model.Id;
        _host?.SetSetting("selectedModel", model.Id);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        // Settings can change during the upload, so the model is read once.
        if (!IsConfigured || SelectedModelId is not { } modelId)
            throw new PluginRequestException(
                "Plugin not configured. API key and model required.", PluginRequestFailureKind.Configuration
            );

        var speakerLabels = SpeakerLabels;
        var normalizedLanguage = AssemblyAiModels.NormalizeLanguage(language);
        var keyterms = KeytermsFor(prompt, UseDictionaryTerms, BudgetFor(modelId));
        var duration = WavDuration(wavAudio);
        if (SyncRequestFor(AssemblyAiModels.Resolve(modelId), Mode, speakerLabels, normalizedLanguage, keyterms, duration, wavAudio.Length)
            is { } sync)
        {
            try
            {
                return await TranscribeSyncAsync(wavAudio, sync, duration, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && ex is PluginRequestException { FailureKind: not PluginRequestFailureKind.Authentication }
                    or OperationCanceledException)
            {
                // The async API takes any recording the Sync API refuses or fails on; a bad key would fail there too.
                var reason = ex is PluginRequestException request ? request.FailureKind.ToString() : "Timeout";
                _host?.Log(PluginLogLevel.Warning, $"AssemblyAI Sync API failed ({reason}); transcribing with the async API.");
            }
        }

        var started = Stopwatch.StartNew();
        var uploadUrl = await UploadAudioAsync(wavAudio, ct);
        var transcriptId = await SubmitTranscriptionAsync(
            new SubmitOptions(uploadUrl, modelId, normalizedLanguage, keyterms, speakerLabels),
            ct);
        var result = await PollForResultAsync(transcriptId, speakerLabels, ct);
        _host?.Log(PluginLogLevel.Info, $"AssemblyAI async API returned the transcript after {started.ElapsedMilliseconds} ms.");
        return result;
    }

    internal sealed record SyncRequest(string ModelId, string Language, IReadOnlyList<string> Keyterms);

    // Null when the async API must take the recording: Async mode, speaker labels, no Sync model, Auto language
    // (Sync reports no detected language, which translation and spelling variants need), or input over the limits.
    internal static SyncRequest? SyncRequestFor(
        AssemblyAiModel model,
        string mode,
        bool speakerLabels,
        string? language,
        IReadOnlyList<string> keyterms,
        TimeSpan? duration,
        long bytes
    )
    {
        if (mode == AssemblyAiModes.Async
            || speakerLabels
            || model.Sync is not { } limits
            || duration > limits.MaxDuration
            || bytes > limits.MaxBytes
            || language is null
            || !limits.Languages.Contains(language)
            || keyterms.Count > limits.MaxTerms
            || keyterms.Sum(static term => term.Length) > limits.MaxTermCharacters)
            return null;

        return new SyncRequest(limits.ModelId, language, keyterms);
    }

    private async Task<PluginTranscriptionResult> TranscribeSyncAsync(
        byte[] wavAudio,
        SyncRequest sync,
        TimeSpan? duration,
        CancellationToken ct
    )
    {
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncUrl);
        request.Headers.Add("Authorization", ApiKey);
        request.Headers.Add("X-AAI-Model", sync.ModelId);
        request.Content = CreateSyncContent(wavAudio, sync);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // AssemblyAI gives up after 30 s; the rest covers the upload.
        deadline.CancelAfter(TimeSpan.FromSeconds(45) + TimeSpan.FromSeconds(wavAudio.Length / 1_000_000.0));

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, deadline.Token,
            (errorResponse, errorBody) => $"AssemblyAI Sync error {(int)errorResponse.StatusCode}: {errorBody}");
        using var doc = await ReadObjectAsync(response, deadline.Token);
        var root = doc.RootElement;
        // A recording without speech has a null text.
        if (!root.TryGetProperty("text", out var textElement)
            || textElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw InvalidResponse();

        var segments = SyncSegments(root);
        var seconds = Number(root, "audio_duration_ms") is { } ms ? ms / 1000 : duration?.TotalSeconds ?? 0;
        if (segments.Count > 0)
            seconds = Math.Max(seconds, segments.Max(static segment => segment.End));
        _host?.Log(PluginLogLevel.Info, $"AssemblyAI Sync API returned the transcript after {started.ElapsedMilliseconds} ms.");
        return new PluginTranscriptionResult(textElement.GetString() ?? "", sync.Language, seconds, NoSpeechProbability: null)
        {
            Segments = segments,
        };
    }

    internal static MultipartFormDataContent CreateSyncContent(byte[] wavAudio, SyncRequest sync)
    {
        var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wavAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "audio", "audio.wav");
        // Word timestamps add at most 0.2 s and give file transcriptions subtitles.
        var config = new Dictionary<string, object>
        {
            ["timestamps"] = true,
            ["language_codes"] = new[] { sync.Language },
        };
        if (sync.Keyterms.Count > 0)
            config["keyterms_prompt"] = sync.Keyterms;
        var json = new StringContent(JsonSerializer.Serialize(config), Encoding.UTF8);
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(json, "config");
        return content;
    }

    // One segment per word. A word the Sync API could not align, or timed with no length, joins the word
    // before it (or the first timed word), so subtitles keep all text and every segment has a duration.
    internal static List<PluginTranscriptionSegment> SyncSegments(JsonElement root)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
            return segments;

        var leading = new List<string>();
        foreach (var word in words.EnumerateArray())
        {
            if (OptionalString(word, "text")?.Trim() is not { Length: > 0 } text)
                continue;

            if (Number(word, "start") is { } start
                && Number(word, "end") is { } end
                && end > start
                && (segments.Count == 0 || start / 1000 >= segments[^1].Start))
            {
                if (leading.Count > 0)
                {
                    text = string.Join(" ", leading) + " " + text;
                    leading.Clear();
                }

                // AssemblyAI times words in milliseconds.
                segments.Add(new PluginTranscriptionSegment(text, start / 1000, end / 1000));
            }
            else if (segments.Count == 0)
                leading.Add(text);
            else
                segments[^1] = segments[^1] with { Text = segments[^1].Text + " " + text };
        }

        return segments;
    }

    // The length of a RIFF/WAVE recording, or null when its header cannot be read.
    internal static TimeSpan? WavDuration(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;

        long byteRate = 0;
        long offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.AsSpan((int)offset, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan((int)offset + 4, 4));
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan((int)body + 8, 4));
            else if (id.SequenceEqual("data"u8))
                // A streamed WAV can leave the data size open, so only the bytes present count.
                return byteRate > 0 ? TimeSpan.FromSeconds(Math.Min(size, wav.Length - body) / (double)byteRate) : null;
            offset = body + size + (size & 1);
        }

        return null;
    }

    private async Task<string> UploadAudioAsync(byte[] wavAudio, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/upload");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new ByteArrayContent(wavAudio);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"AssemblyAI upload error {(int)errorResponse.StatusCode}: {errorBody}");
        using var doc = await ReadObjectAsync(response, ct);
        var uploadUrl = RequiredString(doc.RootElement, "upload_url");
        return Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uploadUrl
            : throw InvalidResponse();
    }

    private async Task<string> SubmitTranscriptionAsync(SubmitOptions options, CancellationToken ct)
    {
        var body = CreateSubmitBody(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/transcript");
        request.Headers.Add("Authorization", ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"AssemblyAI submit error {(int)errorResponse.StatusCode}: {errorBody}");
        using var doc = await ReadObjectAsync(response, ct);
        return RequiredString(doc.RootElement, "id");
    }

    internal sealed record SubmitOptions(
        string AudioUrl,
        string ModelId,
        string? Language,
        IReadOnlyList<string> Keyterms,
        bool SpeakerLabels = false
    );

    internal static Dictionary<string, object> CreateSubmitBody(SubmitOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["audio_url"] = options.AudioUrl,
            ["speech_models"] = new[] { options.ModelId },
        };

        if (options.Language is null)
            body["language_detection"] = true;
        else
            body["language_code"] = options.Language;

        if (options.Keyterms.Count > 0)
            body["keyterms_prompt"] = options.Keyterms;

        if (options.SpeakerLabels)
            body["speaker_labels"] = true;

        return body;
    }

    internal static DictionaryTermsBudget BudgetFor(string? modelId) => AssemblyAiModels.Resolve(modelId).Budget;

    // Dictionary terms stay whole; prompt text is not sent (AssemblyAI's prompt describes the audio).
    // Terms are dropped when the setting is off, so a prompt composed before a toggle cannot add them.
    internal static IReadOnlyList<string> KeytermsFor(
        string? prompt,
        bool useDictionaryTerms,
        DictionaryTermsBudget budget
    )
    {
        if (!useDictionaryTerms)
            return [];

        var terms = PluginDictionaryTerms.Clip(PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms, budget);
        var limited = new List<string>(terms.Count);
        var totalWords = 0;
        foreach (var term in terms)
        {
            totalWords += term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            if (totalWords > budget.MaxTerms)
                break;
            limited.Add(term);
        }

        return limited;
    }

    internal static IReadOnlyList<string> StreamingKeytermsFor(string? prompt, bool useDictionaryTerms)
    {
        if (!useDictionaryTerms)
            return [];

        var terms = PluginDictionaryTerms.Clip(PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms, s_streamingBudget);
        var limited = new List<string>(terms.Count);
        var length = "%5B%5D".Length;
        foreach (var term in terms)
        {
            length += Uri.EscapeDataString(JsonSerializer.Serialize(term)).Length
                + (limited.Count == 0 ? 0 : "%2C".Length);
            if (length > MaxStreamingKeytermsQueryLength)
                break;
            limited.Add(term);
        }

        return limited;
    }

    private async Task<PluginTranscriptionResult> PollForResultAsync(
        string transcriptId,
        bool speakerLabels,
        CancellationToken ct
    )
    {
        for (var i = 0; i < 300; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl}/v2/transcript/{Uri.EscapeDataString(transcriptId)}"
            );
            request.Headers.Add("Authorization", ApiKey);

            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
                (errorResponse, errorBody) => $"AssemblyAI poll error {(int)errorResponse.StatusCode}: {errorBody}");
            using var doc = await ReadObjectAsync(response, ct);
            var root = doc.RootElement;

            switch (RequiredString(root, "status"))
            {
                case "queued" or "processing":
                    continue;
                case "completed":
                    return ParseCompleted(root, speakerLabels);
                case "error":
                    throw new PluginRequestException(
                        $"AssemblyAI transcription failed: {OptionalString(root, "error") ?? "Unknown error"}",
                        PluginRequestFailureKind.InvalidRequest);
                default:
                    throw InvalidResponse();
            }
        }

        throw new TimeoutException("AssemblyAI transcription timed out after 5 minutes");
    }

    internal static PluginTranscriptionResult ParseCompleted(JsonElement root, bool speakerLabels)
    {
        // A completed recording without speech has a null text.
        if (!root.TryGetProperty("text", out var textElement)
            || textElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw InvalidResponse();

        var text = textElement.GetString() ?? "";
        var duration = Number(root, "audio_duration") ?? 0;
        // Without usable utterances, labelled transcripts keep the plain text.
        var segments = speakerLabels ? ReadUtterances(root) : [];
        // ReSharper disable once InvertIf -- inverting would duplicate the result construction below.
        if (segments.Count > 0)
        {
            text = string.Join("\n", segments.Select(static segment => segment.Text));
            duration = Math.Max(duration, segments.Max(static segment => segment.End));
        }

        return new PluginTranscriptionResult(
            text,
            OptionalString(root, "language_code"),
            duration,
            NoSpeechProbability: null
        )
        {
            Segments = segments,
        };
    }

    private static List<PluginTranscriptionSegment> ReadUtterances(JsonElement root)
    {
        var segments = new List<PluginTranscriptionSegment>();
        if (!root.TryGetProperty("utterances", out var utterances) || utterances.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var utterance in utterances.EnumerateArray())
        {
            if (OptionalString(utterance, "text")?.Trim() is not { Length: > 0 } text)
                continue;
            // Dropping a spoken utterance would lose text, so labelled output needs every one.
            if (Number(utterance, "start") is not { } start
                || Number(utterance, "end") is not { } end
                || end < start)
                return [];

            if (OptionalString(utterance, "speaker")?.Trim() is { Length: > 0 } speaker
                && !speaker.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                text = $"Speaker {speaker}: {text}";
            // AssemblyAI times utterances in milliseconds.
            segments.Add(new PluginTranscriptionSegment(text, start / 1000, end / 1000));
        }

        return segments;
    }

    private static async Task<JsonDocument> ReadObjectAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            return doc;
        doc.Dispose();
        throw InvalidResponse();
    }

    private static PluginRequestException InvalidResponse() =>
        new("AssemblyAI returned an invalid or incomplete response.", PluginRequestFailureKind.OutputIncomplete);

    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : throw InvalidResponse();

    private static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
        && double.IsFinite(value)
        && value >= 0
            ? value
            : null;

    internal string? ApiKey { get; private set; }
    internal bool UseDictionaryTerms { get; private set; }
    internal bool SpeakerLabels { get; private set; }
    internal string Mode { get; private set; } = AssemblyAiModes.Live;

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey);

            _host.NotifyCapabilitiesChanged();
        }
    }

    internal async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/v2/transcript?limit=1"
        );
        request.Headers.Add("Authorization", apiKey);
        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                "aa...",
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: s_models.Select(m => new PluginSettingOption(m.Id, m.DisplayName)).ToList()
            ),
            new(
                UseDictionaryTermsSettingName,
                Loc.L("Settings.UseDictionaryTerms"),
                Description: Loc.L("Settings.UseDictionaryTermsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
            new(
                TranscriptionModeSettingName,
                Loc.L("Settings.TranscriptionMode"),
                Description: Loc.L("Settings.TranscriptionModeDescription"),
                Options:
                [
                    new PluginSettingOption(AssemblyAiModes.Live, Loc.L("Settings.TranscriptionMode.Live")),
                    new PluginSettingOption(AssemblyAiModes.Sync, Loc.L("Settings.TranscriptionMode.Sync")),
                    new PluginSettingOption(AssemblyAiModes.Async, Loc.L("Settings.TranscriptionMode.Async")),
                ]
            ),
            new(
                SpeakerLabelsSettingName,
                Loc.L("Settings.SpeakerLabels"),
                Description: Loc.L("Settings.SpeakerLabelsDescription"),
                Kind: PluginSettingKind.Boolean
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => ApiKey,
                "selectedModel" => SelectedModelId,
                UseDictionaryTermsSettingName => UseDictionaryTerms ? "true" : "false",
                SpeakerLabelsSettingName => SpeakerLabels ? "true" : "false",
                TranscriptionModeSettingName => Mode,
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
                await SetApiKeyAsync(value ?? string.Empty);
                break;
            case "selectedModel":
                if (!string.IsNullOrWhiteSpace(value))
                    SelectModel(value);
                break;
            case UseDictionaryTermsSettingName:
                UseDictionaryTerms = bool.TryParse(value, out var useDictionaryTerms) && useDictionaryTerms;
                _host?.SetSetting(UseDictionaryTermsSettingName, UseDictionaryTerms);
                break;
            case SpeakerLabelsSettingName:
                var speakerLabels = bool.TryParse(value, out var parsed) && parsed;
                if (speakerLabels == SpeakerLabels)
                    break;
                SpeakerLabels = speakerLabels;
                _host?.SetSetting(SpeakerLabelsSettingName, SpeakerLabels);
                // Live text availability follows this setting.
                _host?.NotifyCapabilitiesChanged();
                break;
            case TranscriptionModeSettingName:
                var mode = AssemblyAiModes.Normalize(value);
                if (mode == Mode)
                    break;
                Mode = mode;
                _host?.SetSetting(TranscriptionModeSettingName, Mode);
                _host?.NotifyCapabilitiesChanged();
                break;
        }
    }

    public async Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var valid = await ValidateApiKeyAsync(ApiKey, ct);
        return valid
            ? new PluginSettingsValidationResult(true, Loc.L("Settings.ApiKeyValid"))
            : new PluginSettingsValidationResult(false, Loc.L("Settings.ApiKeyInvalid"));
    }
}
