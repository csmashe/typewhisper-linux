using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.SmallestAi;

/// <summary>Lightning and Lightning Pro speech synthesis, sharing the Pulse API key.</summary>
public sealed partial class SmallestAiPlugin : ITtsProviderPlugin, IPluginSettingsActions
{
    private const string SpeechEndpoint = $"{BaseUrl}/waves/v1/tts";
    private const string SpeechVoicesSettingName = "speechVoices";
    private const string SpeechVoiceSettingName = "speechVoice";
    private const string SpeechSpeedSettingName = "speechSpeed";
    private const string RefreshVoicesActionId = "refresh-voices";
    private const string StandardModel = "lightning_v3.1";
    private const string ProModel = "lightning_v3.1_pro";
    private const int SpeechSampleRate = 24_000;

    internal const int MaximumSpeechTextLength = 8_000;

    // Rough speaking rate at 1x. Han, kana and Hangul carry about three Latin letters' worth of speech each.
    private const double SpeechUnitsPerSecond = 15;
    private const int DenseScriptWeight = 3;
    internal const int MaximumSpeechAudioBytes = 12 * 1024 * 1024;
    private static readonly TimeSpan s_maximumSpeechDuration = TimeSpan.FromMinutes(2);

    // HttpClient's own timeout stops at the headers, so this one also bounds the audio body.
    internal TimeSpan SpeechResponseDeadline { get; set; } = TimeSpan.FromSeconds(120);

    private static readonly double[] s_speeds = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];

    private static readonly SmallestAiVoice s_defaultVoice = new("magnus", "Magnus", StandardModel, "en");

    // Request codes from the synthesis reference; Pro adds the East Asian, Nordic and Middle Eastern set.
    private static readonly HashSet<string> s_standardLanguages =
    [
        "en", "es", "fr", "de", "it", "nl", "sv", "pt", "pl", "ru",
        "hi", "mr", "gu", "pa", "bn", "or", "ta", "te", "kn", "ml",
    ];

    private static readonly HashSet<string> s_proLanguages =
    [
        .. s_standardLanguages,
        "el", "fi", "no", "zh", "ja", "ko", "id", "ms", "vi", "tr", "ar",
    ];

    private SmallestAiVoice[] _voices = [s_defaultVoice];
    private string? _selectedVoiceId;

    private SmallestAiVoice[] Voices => Volatile.Read(ref _voices);

    private double SpeechSpeed { get; set; } = 1;

    // ITtsProviderPlugin

    public IReadOnlyList<PluginVoiceInfo> AvailableVoices =>
        Voices.Select(voice => new PluginVoiceInfo(voice.Key, voice.DisplayName, voice.Language)).ToList();

    // A selection a later refresh dropped falls back to the provider default.
    public string? SelectedVoiceId =>
        _selectedVoiceId is { } id && Voices.Any(voice => voice.Key == id) ? id : null;

    public string SettingsSummary =>
        $"{ResolveVoice(SelectedVoiceId).DisplayName}; {FormatSpeed(SpeechSpeed)}×";

    public void SelectVoice(string? voiceId)
    {
        _selectedVoiceId = voiceId is not null && Voices.Any(voice => voice.Key == voiceId) ? voiceId : null;
        _host?.SetSetting(SpeechVoiceSettingName, _selectedVoiceId);
    }

    public async Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new PluginRequestException(Loc.L("Settings.NotConfiguredApiKeyRequired"), PluginRequestFailureKind.Configuration);

        var text = request.Text.Trim();
        if (text.Length == 0)
            return SmallestAiInactivePlaybackSession.Instance;

        // The audio limits are only checked after the paid request, so refuse text that would exceed them.
        if (text.Length > MaximumSpeechTextLength || EstimateSpeechDuration(text, SpeechSpeed) > s_maximumSpeechDuration)
        {
            throw new PluginRequestException(
                Loc.L("Speech.TextTooLong"),
                PluginRequestFailureKind.RequestTooLarge);
        }

        var host = _host ?? throw new InvalidOperationException("Smallest AI plugin is not activated.");
        // Paid endpoint: preflight playback before issuing a request that couldn't be played.
        if (!host.PcmPlayback.IsAvailable)
        {
            host.Log(PluginLogLevel.Warning, "Skipping Smallest AI speech request: no supported PCM audio player is available.");
            return SmallestAiInactivePlaybackSession.Instance;
        }

        var voice = ResolveVoice(SelectedVoiceId);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        // The API requires audio/wav here whatever the output format.
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));
        httpRequest.Content = JsonContent.Create(new
        {
            text,
            voice_id = voice.Id,
            // A voice only speaks correctly with the model whose pool it came from.
            model = voice.Model,
            sample_rate = SpeechSampleRate,
            output_format = "wav",
            speed = SpeechSpeed,
            language = SpeechLanguage(request.Language, voice.Model),
        });

        var wav = await DownloadSpeechAudioAsync(httpRequest, ct);
        var audio = SmallestAiWav.Parse(wav)
            ?? throw new PluginRequestException(Loc.L("Speech.UnplayableAudio"), PluginRequestFailureKind.EmptyResponse);
        if (audio.Duration > s_maximumSpeechDuration)
            throw new PluginRequestException(Loc.L("Speech.AudioTooLong"), PluginRequestFailureKind.RequestTooLarge);

        return await host.PcmPlayback.PlayAsync(
            new PcmPlaybackRequest(audio.Samples, audio.SampleRate, audio.Channels, PcmSampleFormat.Signed16LittleEndian),
            ct);
    }

    // The default stays Magnus when the catalog has him, not whichever voice sorts first.
    internal static TimeSpan EstimateSpeechDuration(string text, double speed)
    {
        var units = 0;
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- LINQ would box the struct rune enumerator.
        foreach (var rune in text.EnumerateRunes())
            units += IsDenseScript(rune.Value) ? DenseScriptWeight : 1;
        return TimeSpan.FromSeconds(units / (SpeechUnitsPerSecond * speed));
    }

    private static bool IsDenseScript(int codePoint) =>
        codePoint is >= 0x3040 and <= 0x30FF // Hiragana, Katakana
            or >= 0x3400 and <= 0x4DBF // CJK extension A
            or >= 0x4E00 and <= 0x9FFF // CJK unified ideographs
            or >= 0xAC00 and <= 0xD7AF // Hangul syllables
            or >= 0xF900 and <= 0xFAFF // CJK compatibility ideographs
            or >= 0x20000 and <= 0x3134F; // CJK extensions B–G

    private SmallestAiVoice ResolveVoice(string? key)
    {
        var voices = Voices;
        return voices.FirstOrDefault(voice => voice.Key == key)
            ?? voices.FirstOrDefault(voice => voice.Key == s_defaultVoice.Key)
            ?? voices.FirstOrDefault(voice => voice is { Model: StandardModel, Language: "en" })
            ?? voices[0];
    }

    /// <summary>Drops a refreshed catalog and selection, which belong to the previous key's account.</summary>
    private bool ResetVoiceCatalog()
    {
        var hadCatalog = !(Voices is [var only] && only == s_defaultVoice);
        Volatile.Write(ref _voices, [s_defaultVoice]);
        _selectedVoiceId = null;
        _host?.SetSetting<SmallestAiVoice[]?>(SpeechVoicesSettingName, null);
        _host?.SetSetting<string?>(SpeechVoiceSettingName, null);
        return hadCatalog;
    }

    internal static string SpeechLanguage(string? language, string model)
    {
        var code = NormalizeLanguage(language)?.Split('-', '_')[0].ToLowerInvariant();
        var supported = model == ProModel ? s_proLanguages : s_standardLanguages;
        // Codes a model doesn't know come back English-pronounced, so let the API detect instead.
        return code is not null && supported.Contains(code) ? code : "auto";
    }

    private async Task<byte[]> DownloadSpeechAudioAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SpeechResponseDeadline);
        try
        {
            using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
                _httpClient, request, HttpCompletionOption.ResponseHeadersRead, deadline.Token,
                (errorResponse, errorBody) => $"Smallest AI speech API error {(int)errorResponse.StatusCode}: {ExtractApiError(errorBody)}");
            return await ReadSpeechAudioAsync(response, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                Loc.L("Speech.TimedOut"), PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    private async Task<byte[]> ReadSpeechAudioAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumSpeechAudioBytes)
            throw SpeechAudioTooLarge();

        return await OpenAiApiHelper.ReadBodyWithErrorHandlingAsync(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaximumSpeechAudioBytes)
                    throw SpeechAudioTooLarge();
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }, ct);
    }

    private PluginRequestException SpeechAudioTooLarge() =>
        new(Loc.L("Speech.AudioTooLarge"), PluginRequestFailureKind.RequestTooLarge);

    // Voice catalog

    private void RestoreSpeechSettings(IPluginHostServices host)
    {
        var cached = host.GetSetting<List<SmallestAiVoice>>(SpeechVoicesSettingName);
        var voices = NormalizeVoices(cached ?? []);
        Volatile.Write(ref _voices, voices.Length > 0 ? voices : [s_defaultVoice]);
        _selectedVoiceId = host.GetSetting<string>(SpeechVoiceSettingName);
        var speed = host.GetSetting<double?>(SpeechSpeedSettingName);
        SpeechSpeed = speed is { } value && s_speeds.Contains(value) ? value : 1;
    }

    /// <summary>Fetches both voice pools; a failed refresh keeps the previous catalog.</summary>
    internal async Task<PluginSettingsValidationResult> RefreshVoicesAsync(CancellationToken ct)
    {
        var host = _host;
        var apiKey = ApiKey;
        if (host is null || string.IsNullOrEmpty(apiKey))
            return new PluginSettingsValidationResult(false, Loc.L("Settings.EnterApiKey"));

        var voices = new List<SmallestAiVoice>();
        try
        {
            foreach (var model in new[] { StandardModel, ProModel })
            {
                // The catalog path spells the model with hyphens; synthesis uses underscores.
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, $"{BaseUrl}/waves/v1/{model.Replace('_', '-')}/get_voices");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
                voices.AddRange(ParseVoices(await response.Content.ReadAsStringAsync(ct), model));
            }
        }
        catch (PluginRequestException ex)
        {
            return new PluginSettingsValidationResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new PluginSettingsValidationResult(false, Loc.L("Speech.InvalidVoiceCatalog"));
        }

        var normalized = NormalizeVoices(voices);
        if (normalized.Length == 0)
            return new PluginSettingsValidationResult(false, Loc.L("Speech.NoVoices"));

        // Under the key-write lock, so a key change can't land between the check and the commit.
        await _apiKeyWriteLock.WaitAsync(ct);
        try
        {
            // A key saved or a plugin reactivated mid-refresh would otherwise get the old account's voices.
            if (!ReferenceEquals(host, _host) || !string.Equals(apiKey, ApiKey, StringComparison.Ordinal))
                return new PluginSettingsValidationResult(false, Loc.L("Speech.ConnectionChanged"));

            host.SetSetting(SpeechVoicesSettingName, normalized);
            Volatile.Write(ref _voices, normalized);
        }
        finally
        {
            _apiKeyWriteLock.Release();
        }

        host.NotifyCapabilitiesChanged();
        return new PluginSettingsValidationResult(true, Loc.L("Speech.VoicesRefreshed", normalized.Length));
    }

    private static List<SmallestAiVoice> ParseVoices(string json, string model)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("voices", out var voices)
            || voices.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Smallest AI returned an invalid voice catalog.");
        }

        return voices.EnumerateArray()
            .Where(voice => voice.ValueKind == JsonValueKind.Object)
            .Select(voice => new SmallestAiVoice(
                GetString(voice, "voiceId")?.Trim() ?? "",
                GetString(voice, "displayName")?.Trim() ?? "",
                model,
                VoiceLanguage(voice)))
            .ToList();
    }

    private static SmallestAiVoice[] NormalizeVoices(IEnumerable<SmallestAiVoice?> voices) =>
        voices
            .OfType<SmallestAiVoice>()
            .Where(voice => !string.IsNullOrWhiteSpace(voice.Id)
                && !string.IsNullOrWhiteSpace(voice.Name)
                && voice.Model is StandardModel or ProModel)
            .DistinctBy(voice => voice.Key)
            .OrderBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Model, StringComparer.Ordinal)
            .ToArray();

    private static string? VoiceLanguage(JsonElement voice)
    {
        if (!voice.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty("language", out var languages)
            || languages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return languages.EnumerateArray()
            .Where(language => language.ValueKind == JsonValueKind.String)
            .Select(language => LanguageCode(language.GetString()))
            .FirstOrDefault(code => code is not null);
    }

    // The catalog tags languages by English name; the host shows a locale code beside each voice.
    private static string? LanguageCode(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "english" => "en", "german" => "de", "french" => "fr", "spanish" => "es", "italian" => "it",
        "portuguese" => "pt", "dutch" => "nl", "swedish" => "sv", "polish" => "pl", "russian" => "ru",
        "greek" => "el", "finnish" => "fi", "norwegian" => "no", "hindi" => "hi", "marathi" => "mr",
        "gujarati" => "gu", "punjabi" => "pa", "bengali" => "bn", "odia" => "or", "tamil" => "ta",
        "telugu" => "te", "kannada" => "kn", "malayalam" => "ml", "chinese" => "zh", "japanese" => "ja",
        "korean" => "ko", "indonesian" => "id", "malay" => "ms", "vietnamese" => "vi", "turkish" => "tr",
        "arabic" => "ar",
        { Length: 2 } code when code.All(char.IsAsciiLetterLower) => code,
        _ => null,
    };

    // Speech speed setting

    private PluginSettingDefinition SpeechSpeedSettingDefinition() =>
        new(
            SpeechSpeedSettingName,
            Loc.L("Settings.SpeechSpeed"),
            Description: Loc.L("Settings.SpeechSpeedDescription"),
            Options: s_speeds
                .Select(speed => new PluginSettingOption(FormatSpeed(speed), $"{FormatSpeed(speed)}×"))
                .ToList());

    private void SetSpeechSpeed(string? value)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var speed)
            || !s_speeds.Contains(speed))
        {
            return;
        }

        _host?.SetSetting(SpeechSpeedSettingName, speed);
        SpeechSpeed = speed;
    }

    private static string FormatSpeed(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture);

    // IPluginSettingsActions

    public IReadOnlyList<PluginSettingsAction> GetSettingsActions() =>
        [
            new(
                RefreshVoicesActionId,
                Loc.L("Settings.RefreshVoices"),
                Loc.L("Settings.RefreshVoicesDescription"),
                IsEnabled: IsConfigured),
        ];

    public Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(string actionId, CancellationToken ct) =>
        actionId == RefreshVoicesActionId
            ? RefreshVoicesAsync(ct)
            : throw new ArgumentException($"Unknown action: {actionId}", nameof(actionId));
}

/// <summary>A catalog voice paired with the model pool it must be synthesized with.</summary>
internal sealed record SmallestAiVoice(string Id, string Name, string Model, string? Language)
{
    public string Key => $"{Model}:{Id}";

    public string DisplayName => Model.EndsWith("_pro", StringComparison.Ordinal)
        ? $"{Name} · Lightning Pro"
        : $"{Name} · Lightning";
}

internal sealed class SmallestAiInactivePlaybackSession : ITtsPlaybackSession
{
    public static SmallestAiInactivePlaybackSession Instance { get; } = new();

    private SmallestAiInactivePlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add => value?.Invoke(this, EventArgs.Empty);
        remove { }
    }

    public void Stop()
    {
    }
}
