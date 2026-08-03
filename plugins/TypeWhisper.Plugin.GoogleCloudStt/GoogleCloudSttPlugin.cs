// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.GoogleCloudStt;

// NOTE (2026-05-29): This plugin is intentionally BATCH-ONLY for now — it does not
// implement real-time streaming (no SupportsStreaming override). Other cloud STT
// providers here stream over a WebSocket reusing their existing API key, but Google
// has no REST/WebSocket real-time API: StreamingRecognize is gRPC-only. Adding it
// would mean a heavy new gRPC + protobuf dependency (with AOT-trim risk) AND
// migrating auth from the plain API key used below to a service-account / ADC
// credential. That is a research spike, not a drop-in change, so it is parked:
// leaving the plugin as-is until that cost is justified or a streaming reference
// to follow exists.
public sealed class GoogleCloudSttPlugin
    : ITranscriptionEnginePlugin,
        IPluginSettingsProvider,
        IPluginLocalizationAware
{
    private const string ApiEndpoint = "https://speech.googleapis.com/v1/speech:recognize";
    private const int SampleRateHertz = 16000;
    private const int BytesPerSample = sizeof(short);
    private const int BytesPerSecond = SampleRateHertz * BytesPerSample;
    private const int MaxChunkSeconds = 55;
    private const int MaxChunkBytes = MaxChunkSeconds * BytesPerSecond;
    private const int BoundarySearchSeconds = 5;
    private const int BoundarySearchBytes = BoundarySearchSeconds * BytesPerSecond;
    private const int QuietWindowMilliseconds = 20;
    private const int QuietWindowBytes =
        SampleRateHertz * BytesPerSample * QuietWindowMilliseconds / 1000;

    private readonly HttpClient _httpClient;
    private IPluginHostServices? _host;
    private string? _apiKey;

    public GoogleCloudSttPlugin()
        : this(new HttpClientHandler()) { }

    // Bounds each request round trip, not the total segmented transcription. A 55s chunk has
    // ample headroom for its ~2.3 MB base64 upload; 120s matches the other cloud STT plugins here.
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(120);

    // Test seam: lets a stub handler answer requests without hitting the network.
    internal GoogleCloudSttPlugin(HttpMessageHandler handler) =>
        _httpClient = new HttpClient(handler) { Timeout = s_requestTimeout };

    public string PluginId => "com.typewhisper.google-cloud-stt";
    public string PluginName => "Google Cloud STT";
    public string PluginVersion => "1.0.0";

    public async Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        _apiKey = await host.LoadSecretAsync("api-key");
        SelectedModelId = host.GetSetting<string>("selectedModel") ?? "latest_long";
        host.Log(PluginLogLevel.Info, $"Activated (configured={IsConfigured})");
    }

    public Task DeactivateAsync()
    {
        _host = null;
        return Task.CompletedTask;
    }

    public string ProviderId => "google-cloud-stt";
    public string ProviderDisplayName => "Google Cloud";
    public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

    public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
    [new("latest_long", "Google Cloud (Long)")];

    public string? SelectedModelId { get; private set; }

    public bool SupportsTranslation => false;

    public void SelectModel(string modelId)
    {
        if (modelId != "latest_long")
            throw new ArgumentException($"Unknown model: {modelId}");
        SelectedModelId = modelId;
        _host?.SetSetting("selectedModel", modelId);
    }

    public async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wavAudio,
        string? language,
        bool translate,
        string? prompt,
        CancellationToken ct
    )
    {
        if (!IsConfigured)
            throw new InvalidOperationException(Loc.L("Settings.NotConfiguredApiKeyRequired"));

        // Google's LINEAR16 encoding wants raw PCM, not a WAV container. ffmpeg's
        // pipe output carries an extra LIST chunk (78-byte header, not 44), so
        // locate the data chunk instead of stripping a fixed 44 bytes.
        var (pcmOffset, pcmByteCount) = LocatePcmData(wavAudio);

        if (pcmByteCount % BytesPerSample != 0)
            throw new InvalidOperationException(
                "Google Cloud STT requires sample-aligned 16-bit PCM audio."
            );

        var langCode = !string.IsNullOrEmpty(language) && language != "auto" ? language : "en-US";
        // Google requires BCP-47; the rest of the app uses ISO-639-1 ("en"),
        // so expand 2-letter codes to a regional variant before sending.
        if (langCode.Length == 2)
            langCode = MapToGoogleLanguageCode(langCode);

        var transcripts = new List<string>();
        string? detectedLanguage = null;
        double totalDuration = 0;
        var chunkOffset = pcmOffset;
        var pcmEnd = checked(pcmOffset + pcmByteCount);

        // Preserve the existing behavior for an empty payload: it still makes one request.
        do
        {
            ct.ThrowIfCancellationRequested();

            var remaining = pcmEnd - chunkOffset;
            var chunkByteCount =
                remaining <= MaxChunkBytes
                    ? remaining
                    : FindQuietBoundary(wavAudio, chunkOffset);
            var chunkResult = await TranscribeChunkAsync(
                wavAudio,
                chunkOffset,
                chunkByteCount,
                langCode,
                ct
            );

            if (!string.IsNullOrEmpty(chunkResult.Text))
                transcripts.Add(chunkResult.Text);
            detectedLanguage ??= chunkResult.DetectedLanguage;
            totalDuration += chunkResult.DurationSeconds;
            chunkOffset += chunkByteCount;
        } while (chunkOffset < pcmEnd);

        return new PluginTranscriptionResult(
            string.Join(' ', transcripts),
            detectedLanguage ?? langCode,
            totalDuration
        );
    }

    private async Task<ChunkTranscriptionResult> TranscribeChunkAsync(
        byte[] wavAudio,
        int pcmOffset,
        int pcmByteCount,
        string langCode,
        CancellationToken ct
    )
    {
        var audioBase64 = Convert.ToBase64String(wavAudio, pcmOffset, pcmByteCount);
        var requestBody = new
        {
            config = new
            {
                encoding = "LINEAR16",
                sampleRateHertz = SampleRateHertz,
                languageCode = langCode,
                model = "latest_long",
            },
            audio = new { content = audioBase64 },
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiEndpoint}?key={_apiKey}");
        request.Content = content;

        // Default ResponseContentRead keeps HttpClient.Timeout covering the response-body read;
        // ResponseHeadersRead would end the timeout at the headers and let a stalled body hang
        // when the caller passes CancellationToken.None.
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(responseJson);
    }

    private static int FindQuietBoundary(byte[] wavAudio, int chunkOffset)
    {
        var nominalEnd = chunkOffset + MaxChunkBytes;
        var searchStart = nominalEnd - BoundarySearchBytes;
        var quietestWindowStart = nominalEnd - QuietWindowBytes;
        var quietestScore = long.MaxValue;

        for (
            var windowStart = searchStart;
            windowStart + QuietWindowBytes <= nominalEnd;
            windowStart += QuietWindowBytes
        )
        {
            long score = 0;
            for (
                var sampleOffset = windowStart;
                sampleOffset < windowStart + QuietWindowBytes;
                sampleOffset += BytesPerSample
            )
            {
                var sample = BinaryPrimitives.ReadInt16LittleEndian(
                    wavAudio.AsSpan(sampleOffset, BytesPerSample)
                );
                score += Math.Abs((int)sample);
            }

            // Prefer the later window when scores tie so uniformly quiet audio stays
            // as close as possible to the nominal 55-second boundary.
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (score <= quietestScore)
            {
                quietestScore = score;
                quietestWindowStart = windowStart;
            }
        }

        // Splitting at the center leaves 10 ms of the quiet window on both chunks.
        return quietestWindowStart - chunkOffset + QuietWindowBytes / 2;
    }

    // ffmpeg's piped WAV output writes 0xffffffff placeholder chunk sizes (it
    // can't seek back on a pipe), so trust the declared size only when it fits
    // in the buffer; otherwise use the bytes remaining.
    private static (int Offset, int Length) LocatePcmData(byte[] wavAudio)
    {
        var data = wavAudio.AsSpan();
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            return Fallback(wavAudio.Length);

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkId = data.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var bodyOffset = offset + 8;
            if (chunkId.SequenceEqual("data"u8))
            {
                var remaining = data.Length - bodyOffset;
                var length = chunkSize <= (uint)remaining ? (int)chunkSize : remaining;
                return (bodyOffset, length);
            }

            // Chunks are word-aligned: an odd body is followed by a pad byte.
            var advance = bodyOffset + chunkSize + (chunkSize & 1);
            if (advance <= offset || advance > data.Length)
                break;
            offset = (int)advance;
        }

        return Fallback(wavAudio.Length);

        static (int Offset, int Length) Fallback(int totalLength) =>
            totalLength > 44 ? (44, totalLength - 44) : (0, totalLength);
    }

    private static ChunkTranscriptionResult ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidResponse("the root value must be an object");

        var sb = new StringBuilder();

        if (root.TryGetProperty("results", out var results))
        {
            if (results.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("'results' must be an array");

            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object)
                    throw InvalidResponse("each result must be an object");
                if (!result.TryGetProperty("alternatives", out var alternatives))
                    throw InvalidResponse("each result must contain 'alternatives'");
                if (alternatives.ValueKind != JsonValueKind.Array)
                    throw InvalidResponse("'alternatives' must be an array");

                foreach (var alt in alternatives.EnumerateArray())
                {
                    if (alt.ValueKind != JsonValueKind.Object)
                        throw InvalidResponse("each alternative must be an object");
                    if (
                        !alt.TryGetProperty("transcript", out var transcript)
                        || transcript.ValueKind != JsonValueKind.String
                    )
                        throw InvalidResponse("each alternative must contain a string transcript");

                    if (sb.Length > 0)
                        sb.Append(' ');
                    sb.Append(transcript.GetString());
                }
            }
        }

        // v1 has no audio_duration field; totalBilledTime ("15s" / "15.500s")
        // is the closest proxy. Falls back to 0 when absent.
        double duration = 0;
        if (root.TryGetProperty("totalBilledTime", out var billedTime))
        {
            if (billedTime.ValueKind != JsonValueKind.String)
                throw InvalidResponse("'totalBilledTime' must be a duration string");

            var billedStr = billedTime.GetString() ?? string.Empty;
            if (
                billedStr.EndsWith('s')
                && double.TryParse(
                    billedStr[..^1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var secs
                )
            )
            {
                duration = secs;
            }
            else
            {
                throw InvalidResponse("'totalBilledTime' must be a duration string");
            }
        }

        string? detectedLang = null;
        // ReSharper disable once InvertIf -- inverting would duplicate the multi-argument return below; kept nested for clarity.
        if (
            root.TryGetProperty("results", out var resultsForLang)
            && resultsForLang.ValueKind == JsonValueKind.Array
            && resultsForLang.GetArrayLength() > 0
        )
        {
            var first = resultsForLang[0];
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (first.TryGetProperty("languageCode", out var lc))
            {
                if (lc.ValueKind != JsonValueKind.String)
                    throw InvalidResponse("'languageCode' must be a string");
                detectedLang = lc.GetString();
            }
        }

        return new ChunkTranscriptionResult(sb.ToString().Trim(), detectedLang, duration);

        static InvalidOperationException InvalidResponse(string detail) =>
            new($"Invalid Google Cloud STT response: {detail}.");
    }

    private sealed record ChunkTranscriptionResult(
        string Text,
        string? DetectedLanguage,
        double DurationSeconds
    );

    private static string MapToGoogleLanguageCode(string iso) =>
        iso.ToLowerInvariant() switch
        {
            "en" => "en-US",
            "de" => "de-DE",
            "fr" => "fr-FR",
            "es" => "es-ES",
            "it" => "it-IT",
            "pt" => "pt-BR",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "zh" => "zh-CN",
            "ru" => "ru-RU",
            "nl" => "nl-NL",
            "pl" => "pl-PL",
            "sv" => "sv-SE",
            "da" => "da-DK",
            "fi" => "fi-FI",
            "no" => "nb-NO",
            "tr" => "tr-TR",
            "ar" => "ar-SA",
            "hi" => "hi-IN",
            "uk" => "uk-UA",
            "cs" => "cs-CZ",
            _ => iso,
        };

    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    // Prefer the host's localization once activated; fall back to the catalog
    // injected at load so settings labels/validation resolve even when this
    // plugin is disabled (never activated, so _host is null).
    internal IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    internal async Task SetApiKeyAsync(string apiKey)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (_host is not null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                await _host.DeleteSecretAsync("api-key");
            else
                await _host.StoreSecretAsync("api-key", apiKey.Trim());

            _host.NotifyCapabilitiesChanged();
        }
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
        [
            new(
                "api-key",
                Loc.L("Settings.ApiKey"),
                true,
                null,
                Loc.L("Settings.ApiKeyDescription")
            ),
            new(
                "selectedModel",
                Loc.L("Settings.TranscriptionModel"),
                Description: Loc.L("Settings.ModelDescription"),
                Options: TranscriptionModels
                    .Select(m => new PluginSettingOption(m.Id, m.DisplayName))
                    .ToList()
            ),
        ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(
            key switch
            {
                "api-key" => _apiKey,
                "selectedModel" => SelectedModelId,
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
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
