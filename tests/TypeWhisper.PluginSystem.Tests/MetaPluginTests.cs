using System.Runtime.CompilerServices;
using System.Net.WebSockets;
using TypeWhisper.Plugin.Meta;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class MetaPluginTests
{
    [Fact]
    public async Task RefreshAvailableModelsAsync_LoadsAndPartitionsMetaCatalog()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "object": "list",
              "data": [
                {"id":"muse-spark-1.1","object":"model","owned_by":"meta"},
                {"id":"muse-voice-transcribe-1.0","object":"model","owned_by":"meta"},
                {"id":"muse-spark-1.2","object":"model","owned_by":"meta"},
                {"id":"muse-image-1.0","object":"model","owned_by":"meta"}
              ]
            }
            """);
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        var host = new FakePluginHostServices();
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync(" meta-key ");

        var catalog = await sut.RefreshAvailableModelsAsync();

        Assert.NotNull(catalog);
        Assert.Equal(
            ["muse-spark-1.2", "muse-spark-1.1"],
            catalog.LlmModels.Select(model => model.Id));
        Assert.Equal(
            ["muse-voice-transcribe-1.0"],
            catalog.TranscriptionModels.Select(model => model.Id));
        Assert.Equal("https://api.meta.ai/v1/models", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("meta-key", handler.AuthorizationParameter);
        Assert.Equal(2, sut.SupportedModels.Count);
        Assert.Single(sut.TranscriptionModels);
        Assert.True(host.NotifyCapabilitiesChangedCount >= 2);
    }

    [Fact]
    public async Task RefreshAvailableModelsAsync_RemovesModelsMissingFromSuccessfulRefresh()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-1.0"}]}""");
        handler.EnqueueResponse(
            HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-1.2"}]}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");

        await sut.RefreshAvailableModelsAsync();
        var refreshed = await sut.RefreshAvailableModelsAsync();

        Assert.NotNull(refreshed);
        Assert.Empty(refreshed.TranscriptionModels);
        Assert.Equal(0, sut.FetchedTranscriptionModelCount);
        Assert.Equal(
            MetaPlugin.DefaultTranscriptionModelId,
            Assert.Single(sut.TranscriptionModels).Id);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesForkAuthenticationErrorMapping()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            """{"error":{"message":"bad token"}}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ValidateApiKeyAsync("invalid"));

        Assert.Equal("Invalid API key", error.Message);
    }

    [Fact]
    public async Task ProcessAsync_UsesMetaChatCompletionsParameters()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"role":"assistant","content":"Bereinigt"},"finish_reason":"stop"}]}""");
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");
        sut.SetReasoningEffort("high");

        var result = await sut.ProcessAsync(
            "Correct the transcript.",
            "helo",
            "muse-spark-1.2",
            CancellationToken.None);

        Assert.Equal("Bereinigt", result);
        Assert.Equal("https://api.meta.ai/v1/chat/completions", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var root = body.RootElement;
        Assert.Equal("muse-spark-1.2", root.GetProperty("model").GetString());
        Assert.Equal("high", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task TranscribeWithLanguageHintsAsync_SendsMetaMultipartRequestAndParsesResponse()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """
            {
              "sessionId":"session-1",
              "transcript":"Hallo TypeWhisper.",
              "audioDurationMs":1250,
              "turns":[]
            }
            """);
        using var client = new HttpClient(handler);
        using var sut = new MetaPlugin(client);
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");

        var result = await sut.TranscribeWithLanguageHintsAsync(
            CreatePcm16Wav(),
            ["de-DE", "en"],
            translate: false,
            prompt: "TypeWhisper, e7b076c2",
            CancellationToken.None);

        Assert.Equal("Hallo TypeWhisper.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal("https://api.meta.ai/v1/asr/transcribe", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("application/json", handler.Accept);
        using var request = JsonDocument.Parse(handler.MultipartRequest!);
        var root = request.RootElement;
        Assert.Equal("WAV", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("PUSH_TO_TALK", root.GetProperty("mode").GetString());
        Assert.Equal(MetaPlugin.DefaultTranscriptionModelId, root.GetProperty("model").GetString());
        Assert.Equal(["German", "English"], root.GetProperty("languageBias").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["TypeWhisper", "e7b076c2"], root.GetProperty("keywords").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("audio/wav", handler.AudioContentType);
        Assert.Equal(CreatePcm16Wav(), handler.Audio);
        Assert.Contains("muse-voice-transcribe-1.0", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("German", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("English", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("TypeWhisper", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("e7b076c2", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("name=audio", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TranscriptionRequest_UsesDocumentedMetaFieldNames()
    {
        var json = MetaPlugin.CreateTranscriptionRequestJson(
            "muse-voice-transcribe-1.0",
            "PUSH_TO_TALK",
            ["German", "English"],
            ["TypeWhisper"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("WAV", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("PUSH_TO_TALK", root.GetProperty("mode").GetString());
        Assert.Equal("German", root.GetProperty("languageBias")[0].GetString());
        Assert.Equal("TypeWhisper", root.GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public void RealtimeHandshake_AuthenticatesInFirstFrameAndUsesPcm16KHz()
    {
        var json = MetaRealtimeStreamingSession.CreateHandshakeJson(
            "secret",
            "muse-voice-transcribe-1.0",
            "DIARIZATION",
            ["German"],
            ["TypeWhisper"]);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(
            "Bearer secret",
            root.GetProperty("authorization").GetProperty("accessToken").GetString());
        Assert.Equal("PCM_16KHZ", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("DIARIZATION", root.GetProperty("mode").GetString());
        Assert.Equal("CUMULATIVE", root.GetProperty("partialMode").GetString());
        Assert.False(root.GetProperty("emitAudioProgress").GetBoolean());
        Assert.Equal("TypeWhisper", root.GetProperty("keywords")[0].GetString());
    }

    [Fact]
    public async Task MetaIdentityAndStreamingRemainAvailableWithDictionaryPrompt()
    {
        using var sut = new MetaPlugin(new HttpClient(new RecordingHandler(
            HttpStatusCode.OK,
            "{}")));
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("meta-key");

        Assert.Equal("Meta", sut.PluginName);
        Assert.Equal("Meta", sut.ProviderDisplayName);
        Assert.Equal("Meta", sut.ProviderName);
        Assert.Equal("1.0.0", sut.PluginVersion);
        Assert.True(sut.SupportsStreaming);
    }

    [Fact]
    public async Task SpeakerDiarization_PersistsAndSelectsDiarizationMode()
    {
        var host = new FakePluginHostServices();
        using var sut = new MetaPlugin(new HttpClient(new RecordingHandler(
            HttpStatusCode.OK,
            "{}")));
        await sut.ActivateAsync(host);

        sut.SetSpeakerDiarizationEnabled(true);

        Assert.True(sut.SpeakerDiarizationEnabled);
        Assert.Equal("DIARIZATION", sut.TranscriptionMode);
        Assert.True(host.GetSetting<bool>("speakerDiarizationEnabled"));
    }

    [Fact]
    public void DiarizationCollector_ReportsOnlyNewCompletedTurns()
    {
        var collector = new MetaRealtimeTranscriptCollector("DIARIZATION");

        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply("""{"type":"transcript","transcript":"Hallo","final":false}""");
        var speaker = collector.Apply("""{"type":"speaker","label":"A"}""");
        var firstFinal = collector.Apply(
            """{"type":"speechComplete","turnId":1,"transcript":"Hallo zusammen."}""");
        collector.Apply("""{"type":"speechStart","turnId":2}""");
        collector.Apply("""{"type":"speaker","label":"B"}""");
        var secondFinal = collector.Apply(
            """{"type":"speechComplete","turnId":2,"transcript":"Guten Tag."}""");

        Assert.Equal("Speaker A: Hallo", speaker.Transcript?.Text);
        Assert.Equal("Speaker A: Hallo zusammen.", firstFinal.Transcript?.Text);
        Assert.True(firstFinal.IsFinalEvent);
        Assert.Equal(
            "Speaker B: Guten Tag.",
            secondFinal.Transcript?.Text);
        Assert.True(secondFinal.Transcript?.IsFinal);
    }

    [Fact]
    public void PushToTalkCollector_UsesCompletedTurnAsFinalSnapshot()
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");

        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply("""{"type":"transcript","transcript":"Hallo","final":false}""");
        var complete = collector.Apply(
            """{"type":"speechComplete","turnId":1,"transcript":"Hallo Welt."}""");

        Assert.Equal("Hallo Welt.", complete.Transcript?.Text);
        Assert.True(complete.IsFinalEvent);
    }

    [Fact]
    public void PushToTalkCollector_FaultsForTerminalRevision()
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");

        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply(
            """{"type":"speechComplete","turnId":1,"transcript":"Hallo wor"}""");
        Assert.Throws<InvalidOperationException>(() => collector.Apply(
            """{"type":"transcript","transcript":"Hallo Welt.","final":true}"""));
    }

    [Theory]
    [InlineData("de", "German")]
    [InlineData("pt-BR", "Portuguese")]
    [InlineData("zh-CN", "Mandarin Chinese")]
    [InlineData("fil", "Tagalog")]
    public void NormalizeLanguageHints_MapsIsoCodesToMetaLanguageNames(
        string language,
        string expected)
    {
        Assert.Equal(expected, Assert.Single(MetaPlugin.NormalizeLanguageHints([language])));
    }

    [Theory]
    [InlineData("German", "de")]
    [InlineData("de-DE", "de")]
    [InlineData("iw", "he")]
    [InlineData("cmn", "zh")]
    [InlineData("fil", "tl")]
    [InlineData("Klingon", null)]
    [InlineData("auto", null)]
    public void FirstLanguageCode_ReturnsCanonicalAcceptedCode(string language, string? expected)
    {
        Assert.Equal(expected, MetaPlugin.FirstLanguageCode([language]));
    }

    [Fact]
    public void ParseTranscriptionResponse_MapsTurnTimestampsToSeconds()
    {
        var result = MetaPlugin.ParseTranscriptionResponse(
            """
            {
              "transcript":"Hello. Hi.",
              "audioDurationMs":8240,
              "turns":[
                {"turnId":1,"startMs":1520,"endMs":4640,"transcript":"Hello.","speaker":"A"},
                {"turnId":2,"startMs":5900,"endMs":8240,"transcript":"Hi.","speaker":"B"}
              ]
            }
            """,
            requestedLanguage: null);

        Assert.Equal(8.24, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1.52, result.Segments[0].Start);
        Assert.Equal(8.24, result.Segments[1].End);
    }

    [Fact]
    public void ParseTranscriptionResponse_DiarizationPrefixesSpeakerLabels()
    {
        var result = MetaPlugin.ParseTranscriptionResponse(
            """
            {
              "transcript":"Hello. Hi.",
              "audioDurationMs":2000,
              "turns":[
                {"turnId":1,"startMs":0,"endMs":1000,"transcript":"Hello.","speaker":"A"},
                {"turnId":2,"startMs":1000,"endMs":2000,"transcript":"Hi.","speaker":"Speaker B"}
              ]
            }
            """,
            requestedLanguage: "en",
            includeSpeakerLabels: true);

        Assert.Equal("Speaker A: Hello.\nSpeaker B: Hi.", result.Text);
        Assert.Equal("Speaker A: Hello.", result.Segments[0].Text);
        Assert.Equal("Speaker B: Hi.", result.Segments[1].Text);
    }

    [Fact]
    public void Manifest_DeclaresTranscriptionAndLlmCapabilities()
    {
        var manifestPath = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Meta", "manifest.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        Assert.Equal("com.typewhisper.meta", root.GetProperty("id").GetString());
        Assert.Equal("Meta", root.GetProperty("name").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("TypeWhisper.Plugin.Meta.MetaPlugin", root.GetProperty("pluginClass").GetString());
        Assert.Contains(
            "transcription",
            root.GetProperty("categories").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(
            "llm",
            root.GetProperty("categories").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        using var sut = new MetaPlugin();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(PluginDirectory(), "manifest.json")));
        Assert.Equal(document.RootElement.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Locales_HaveMatchingKeysAndTranslatedSettings(string language)
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Join(PluginDirectory(), "Localization", "en.json")))!;
        var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Join(PluginDirectory(), "Localization", language + ".json")))!;
        Assert.Equal(english.Keys.Order(), translated.Keys.Order());
        Assert.Contains("Manifest.Name", translated.Keys);
        Assert.Contains("Manifest.Description", translated.Keys);
        Assert.Contains("Transcript.SpeakerLabel", translated.Keys);
        Assert.All(translated.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        using var sut = new MetaPlugin();
        sut.SetLocalization(new PluginLocalization(PluginDirectory(), language));
        Assert.Equal(translated["Settings.ApiKey"], sut.GetSettingDefinitions()[0].Label);
        foreach (var pair in translated)
        {
            if (language != "en" && pair.Key is not ("Manifest.Name" or "Settings.ReasoningMinimal"))
                Assert.NotEqual(english[pair.Key], pair.Value);
            string[] placeholders = ["{0}", "{1}"];
            foreach (var placeholder in placeholders)
                Assert.Equal(english[pair.Key].Contains(placeholder, StringComparison.Ordinal), pair.Value.Contains(placeholder, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Settings_RoundTripAndNotifyCapabilities()
    {
        var host = new FakePluginHostServices();
        using var sut = new MetaPlugin();
        await sut.ActivateAsync(host);
        Assert.Equal(["api-key", "selectedModel", "speakerDiarizationEnabled", "selectedLlmModel", "reasoningEffort"],
            sut.GetSettingDefinitions().Select(setting => setting.Key));
        Assert.Equal([PluginSettingKind.Secret, PluginSettingKind.Dropdown, PluginSettingKind.Boolean, PluginSettingKind.Dropdown, PluginSettingKind.Dropdown],
            sut.GetSettingDefinitions().Select(setting => setting.Kind));
        Assert.Equal("medium", await sut.GetSettingValueAsync("reasoningEffort"));
        Assert.Equal("false", await sut.GetSettingValueAsync("speakerDiarizationEnabled"));
        var values = new Dictionary<string, string>
        {
            ["api-key"] = "meta-key",
            ["selectedModel"] = MetaPlugin.DefaultTranscriptionModelId,
            ["speakerDiarizationEnabled"] = "true",
            ["selectedLlmModel"] = "muse-spark-1.1",
            ["reasoningEffort"] = "xhigh",
        };
        foreach (var pair in values)
        {
            await sut.SetSettingValueAsync(pair.Key, pair.Value);
            Assert.Equal(pair.Value, await sut.GetSettingValueAsync(pair.Key));
        }
        using var reloaded = new MetaPlugin();
        await reloaded.ActivateAsync(host);
        foreach (var pair in values)
            Assert.Equal(pair.Value, await reloaded.GetSettingValueAsync(pair.Key));
        Assert.True(sut.SupportsLanguageHints);
        Assert.True(sut.SupportsStreaming);
        Assert.True(host.NotifyCapabilitiesChangedCount >= 2);
        await sut.SetSettingValueAsync("api-key", null);
        Assert.False(sut.SupportsStreaming);
        Assert.Empty(host.Secrets);
    }

    [Fact]
    public void Keywords_NormalizeDeduplicateAndClipEveryBudget()
    {
        Assert.Empty(MetaPlugin.ParseKeywords(null));
        Assert.Equal(["TypeWhisper", "Muse Spark"], MetaPlugin.ParseKeywords(" , TypeWhisper, typewhisper, Muse  Spark, "));
        Assert.Equal(100, Assert.Single(MetaPlugin.ParseKeywords(new string('a', 101))).Length);
        Assert.Equal("one two three four five six seven eight", Assert.Single(MetaPlugin.ParseKeywords("one two three four five six seven eight nine")));
        Assert.Equal(100, MetaPlugin.ParseKeywords(string.Join(',', Enumerable.Range(0, 101))).Count);
        var clipped = MetaPlugin.ParseKeywords(string.Join(',', Enumerable.Range(0, 10).Select(i => i + new string('a', 99))));
        Assert.Equal(600, clipped.Sum(term => term.Length));
    }

    [Fact]
    public async Task UnconfiguredRequests_ReportConfigurationFailure()
    {
        using var sut = new MetaPlugin();
        await sut.ActivateAsync(new FakePluginHostServices());
        var transcription = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync([1], null, false, null, CancellationToken.None));
        var llm = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync("", "", "", CancellationToken.None));
        var streaming = await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingAsync(null, CancellationToken.None));
        Assert.All<PluginRequestException>([transcription, llm, streaming], error => Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind));
    }

    [Fact]
    public async Task Catalog_NormalizesSelectionsAndKeyChangeClearsCaches()
    {
        var host = new FakePluginHostServices();
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-new"},{"id":"muse-spark-new"},{"id":"muse-voice-transcribe-new"}]}""");
        using var sut = new MetaPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("first");
        await sut.RefreshModelCatalogAsync();
        Assert.Equal("muse-spark-new", sut.SelectedLlmModelId);
        Assert.Equal("muse-voice-transcribe-new", sut.SelectedModelId);
        Assert.Single(sut.SupportedModels);
        Assert.Contains("1", sut.GetSettingDefinitions()[1].Description);
        await sut.SetApiKeyAsync("second");
        Assert.Equal(0, sut.FetchedLlmModelCount);
        Assert.Equal(0, sut.FetchedTranscriptionModelCount);
        Assert.Equal(MetaPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal(MetaPlugin.DefaultTranscriptionModelId, sut.SelectedModelId);
    }

    [Theory]
    [InlineData("second")]
    [InlineData(null)]
    public async Task SetApiKey_SecretPersistenceFailurePreservesState(string? newKey)
    {
        var host = new FakePluginHostServices();
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"muse-spark-new"},{"id":"muse-voice-transcribe-new"}]}""");
        using var sut = new MetaPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("first");
        await sut.RefreshModelCatalogAsync();
        var notifications = host.NotifyCapabilitiesChangedCount;
        var settings = host.SettingsSnapshot;
        host.FailSecretWrites = true;

        await Assert.ThrowsAsync<IOException>(() => sut.SetApiKeyAsync(newKey));

        Assert.Equal("first", sut.ApiKey);
        Assert.Equal("first", host.Secrets["api-key"]);
        Assert.Equal(1, sut.FetchedLlmModelCount);
        Assert.Equal(1, sut.FetchedTranscriptionModelCount);
        Assert.Equal("muse-spark-new", Assert.Single(sut.SupportedModels).Id);
        Assert.Equal("muse-voice-transcribe-new", Assert.Single(sut.TranscriptionModels).Id);
        Assert.Equal("muse-spark-new", sut.SelectedLlmModelId);
        Assert.Equal("muse-voice-transcribe-new", sut.SelectedModelId);
        Assert.Equal(settings, host.SettingsSnapshot);
        Assert.Equal(notifications, host.NotifyCapabilitiesChangedCount);

        host.FailSecretWrites = false;
        await sut.SetApiKeyAsync(newKey);
        Assert.Equal(newKey, sut.ApiKey);
        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "invalid JSON")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task FailedCatalog_PreservesCachesAndSelections(HttpStatusCode status, string body)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"data":[{"id":"muse-spark-new"},{"id":"muse-voice-transcribe-new"}]}""");
        handler.EnqueueResponse(status, body);
        using var sut = new MetaPlugin(new HttpClient(handler));
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("key");
        await sut.RefreshModelCatalogAsync();
        await sut.RefreshModelCatalogAsync();
        Assert.Equal("muse-spark-new", sut.SelectedLlmModelId);
        Assert.Equal("muse-voice-transcribe-new", sut.SelectedModelId);
        Assert.Equal(1, sut.FetchedLlmModelCount);
        Assert.Equal(1, sut.FetchedTranscriptionModelCount);
    }

    [Fact]
    public async Task Catalog_DiscardsStaleResponseAfterKeyChangesBack()
    {
        var handler = new DelayedCatalogHandler();
        using var sut = new MetaPlugin(new HttpClient(handler));
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("first");
        var refresh = sut.RefreshModelCatalogAsync();
        await handler.Started.Task;
        await sut.SetApiKeyAsync("second");
        await sut.SetApiKeyAsync("first");
        handler.Response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"muse-spark-stale"}]}"""),
        });
        await refresh;
        Assert.Equal(0, sut.FetchedLlmModelCount);
        Assert.Equal(MetaPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK, HttpStatusCode.OK, false, "Settings.EnterApiKey")]
    [InlineData("key", HttpStatusCode.Unauthorized, HttpStatusCode.OK, false, "Settings.ApiKeyInvalid")]
    [InlineData("key", HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, false, "Settings.ValidationFailed")]
    [InlineData("key", HttpStatusCode.OK, HttpStatusCode.OK, true, "Settings.ModelsFetched")]
    [InlineData("key", HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, true, "Settings.ApiKeyValid")]
    public async Task ValidateAsync_ReportsLocalizedOutcomes(string? key, HttpStatusCode validationStatus,
        HttpStatusCode catalogStatus, bool success, string messageKey)
    {
        var handler = new RecordingHandler(validationStatus, "{}");
        handler.EnqueueResponse(catalogStatus, """{"data":[{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-1.0"}]}""");
        var host = new FakePluginHostServices();
        using var sut = new MetaPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync(key);
        var result = await sut.ValidateAsync();
        Assert.NotNull(result);
        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(host.Localization.GetString(messageKey, 1, 1), result.Message);
    }

    [Fact]
    public async Task StreamingFactory_ReceivesNormalizedHintsKeywordsAndMode()
    {
        MetaRealtimeConnectionOptions? captured = null;
        using var sut = new MetaPlugin(new HttpClient(), (options, _) =>
        {
            captured = options;
            return Task.FromResult<IStreamingSession>(new StubSession());
        });
        await sut.ActivateAsync(new FakePluginHostServices());
        await sut.SetApiKeyAsync("key");
        sut.SetSpeakerDiarizationEnabled(true);
        await using var session = await sut.StartStreamingWithLanguageHintsAndPromptAsync(["de-DE", "German", "iw"], "Meta, meta, Muse", CancellationToken.None);
        Assert.NotNull(captured);
        Assert.Equal("key", captured.ApiKey);
        Assert.Equal("DIARIZATION", captured.Mode);
        Assert.Equal(["German", "Hebrew"], captured.LanguageBias);
        Assert.Equal(["Meta", "Muse"], captured.Keywords);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Done")]
    public async Task Realtime_WaitsForReadinessAndTerminalTranscript(string finalText)
    {
        var transport = new ScriptedWebSocketTransport();
        var options = new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "PUSH_TO_TALK", [], []);
        var starting = MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport, options);
        await transport.NextSentAsync();
        Assert.False(starting.IsCompleted);
        transport.EnqueueText("""{"sessionId":"session"}""");
        await using var session = await starting.WaitAsync(TimeSpan.FromSeconds(10));
        byte[] pcm = [1, 2, 3, 4];
        await session.SendAudioAsync(pcm, CancellationToken.None);
        var audio = await transport.NextSentAsync();
        Assert.Equal(WebSocketMessageType.Binary, audio.MessageType);
        Assert.Equal(pcm, audio.Payload.ToArray());
        var finalize = session.FinalizeAsync(CancellationToken.None);
        var end = await transport.NextSentAsync();
        Assert.Equal("{\"type\":\"endStream\"}", Encoding.UTF8.GetString(end.Payload.Span));
        Assert.False(finalize.IsCompleted);
        transport.EnqueueText(JsonSerializer.Serialize(new { type = "transcript", transcript = finalText, final = true }));
        await finalize.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Realtime_EmitsTranscriptCarryingSessionIdAfterHandshake()
    {
        var transport = new ScriptedWebSocketTransport();
        transport.EnqueueText("""{"sessionId":"session"}""");
        await using var session = await MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport,
            new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "PUSH_TO_TALK", [], []));
        await transport.NextSentAsync();
        var events = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Add;
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"sessionId":"session","type":"transcript","transcript":"Done","final":true}""");
        await finalize.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Done", Assert.Single(events).Text);
    }

    [Fact]
    public async Task Realtime_RejectsFirstMessageWithoutSessionIdUsingServerMessage()
    {
        var transport = new ScriptedWebSocketTransport();
        transport.EnqueueText("""{"type":"error","message":"invalid access token"}""");
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport,
                new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "PUSH_TO_TALK", [], [])));
        Assert.Equal("invalid access token", fault.InnerException?.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Speaker A: Hello\nSpeaker B: Hi")]
    [InlineData("Hello Hi")]
    public async Task Realtime_AccumulatesCompletedTurnsWithoutRepeatingTerminalSnapshot(string terminalText)
    {
        var transport = new ScriptedWebSocketTransport();
        transport.EnqueueText("""{"sessionId":"session"}""");
        await using var session = await MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport,
            new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "DIARIZATION", [], []));
        await transport.NextSentAsync();
        var events = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Add;
        transport.EnqueueText("""{"type":"speechStart","turnId":1}""");
        transport.EnqueueText("""{"type":"speaker","label":"A"}""");
        transport.EnqueueText("""{"type":"speechComplete","turnId":1,"transcript":"Hello"}""");
        transport.EnqueueText("""{"type":"speechComplete","turnId":1,"transcript":"Hello"}""");
        transport.EnqueueText("""{"type":"speechStart","turnId":2}""");
        transport.EnqueueText("""{"type":"speaker","label":"Speaker B"}""");
        transport.EnqueueText("""{"type":"transcript","transcript":"H","final":false}""");
        transport.EnqueueText("""{"type":"speechComplete","turnId":2,"transcript":"Hi"}""");
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(JsonSerializer.Serialize(new { type = "transcript", transcript = terminalText, final = true }));
        await finalize.WaitAsync(TimeSpan.FromSeconds(10));

        // Match StreamingTranscriptionCoordinator's append-only final accumulation.
        var finals = events.Where(evt => evt.IsFinal && !string.IsNullOrWhiteSpace(evt.Text)).ToList();
        Assert.Equal("Speaker A: Hello\nSpeaker B: Hi", string.Join("\n", finals.Select(evt => evt.Text.Trim())));
        Assert.Equal(2, finals.Count);
        Assert.Contains(events, evt => evt is { IsFinal: false, Text: "Speaker A: Hello\nSpeaker B: H" });
    }

    [Theory]
    [InlineData(false, "Hello.", "Hello. World.", " World.")]
    [InlineData(true, "Hello.", "Hello. World.", " World.")]
    [InlineData(false, "Hello.", "Hello.", null)]
    [InlineData(true, "Hello.", "Hello.", null)]
    [InlineData(false, "Hello.", "Hello, world.", null)]
    [InlineData(true, "Hello.", "Hello, world.", null)]
    [InlineData(false, "Hello", "Hello.", null)]
    [InlineData(true, "Hello", "Hello.", null)]
    [InlineData(false, "Hello wor", "Hello world.", null)]
    [InlineData(true, "Hello wor", "Hello world.", null)]
    [InlineData(false, "Hello", "Hello\nWorld", "World")]
    [InlineData(false, "Hello.", "Hello.World.", "World.")]
    [InlineData(false, "你好。", "你好。世界。", "世界。")]
    [InlineData(true, "你好。", "你好。世界。", "世界。")]
    [InlineData(false, "Hello.", "Hello. world.", " world.")]
    [InlineData(true, "Hello.", "Hello. world.", " world.")]
    [InlineData(false, "Hello.", "Hello.world.", "world.")]
    [InlineData(false, "Hello…", "Hello…world.", "world.")]
    public async Task Realtime_ReconcilesTerminalAgainstReportedFinal(
        bool activeTurn, string reportedText, string terminalText, string? expectedTail)
    {
        var transport = new ScriptedWebSocketTransport();
        transport.EnqueueText("""{"sessionId":"session"}""");
        await using var session = await MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport,
            new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "PUSH_TO_TALK", [], []));
        await transport.NextSentAsync();
        var events = new List<StreamingTranscriptEvent>();
        session.TranscriptReceived += events.Add;
        transport.EnqueueText("""{"type":"speechStart","turnId":1}""");
        transport.EnqueueText(JsonSerializer.Serialize(new { type = "speechComplete", turnId = 1, transcript = reportedText }));
        if (activeTurn)
            transport.EnqueueText("""{"type":"speechStart","turnId":2}""");
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText(JsonSerializer.Serialize(new { type = "transcript", transcript = terminalText, final = true }));

        if (expectedTail is null && terminalText != reportedText)
        {
            var fault = await Assert.ThrowsAsync<InvalidOperationException>(() => finalize.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("revised previously reported final text", fault.InnerException?.Message);
        }
        else
        {
            await finalize.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var finals = events.Where(evt => evt.IsFinal).Select(evt => evt.Text).ToArray();
        string[] expectedFinals = expectedTail is null ? [reportedText] : [reportedText, expectedTail];
        Assert.Equal(expectedFinals, finals);
    }

    [Theory]
    [InlineData("PUSH_TO_TALK", "Hello world", " world")]
    [InlineData("DIARIZATION", "Hello world", "Speaker B: world")]
    [InlineData("DIARIZATION", "Speaker A: Hello\nSpeaker B: world", "Speaker B: world")]
    [InlineData("DIARIZATION", "", "Speaker B: wor")]
    public void Collector_TerminalFinalizesOnlyUnreportedTail(string mode, string terminalText, string expected)
    {
        var collector = new MetaRealtimeTranscriptCollector(mode);
        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply("""{"type":"speaker","label":"A"}""");
        collector.Apply("""{"type":"speechComplete","turnId":1,"transcript":"Hello"}""");
        collector.Apply("""{"type":"speechStart","turnId":2}""");
        collector.Apply("""{"type":"speaker","label":"B"}""");
        collector.Apply("""{"type":"transcript","transcript":"wor","final":false}""");
        var terminal = collector.Apply(JsonSerializer.Serialize(new { type = "transcript", transcript = terminalText, final = true }));
        Assert.Equal(expected, terminal.Transcript?.Text);
        Assert.True(terminal.Transcript?.IsFinal);
    }

    [Fact]
    public void Collector_LabelsInterimTailBesideSkippedTurnOnEmptyTerminal()
    {
        var collector = new MetaRealtimeTranscriptCollector("DIARIZATION");
        collector.Apply("""{"type":"speechStart","turnId":2}""");
        collector.Apply("""{"type":"speaker","label":"B"}""");
        collector.Apply("""{"type":"speechComplete","turnId":2,"transcript":"Second"}""");
        collector.Apply("""{"type":"speechStart","turnId":3}""");
        collector.Apply("""{"type":"speaker","label":"C"}""");
        collector.Apply("""{"type":"transcript","transcript":"thi","final":false}""");
        var terminal = collector.Apply("""{"type":"transcript","transcript":"","final":true}""");
        Assert.Equal("Speaker B: Second\nSpeaker C: thi", terminal.Transcript?.Text);
    }

    [Fact]
    public async Task Realtime_FinalizeCompletesAfterSubscriberReturns()
    {
        var transport = new ScriptedWebSocketTransport();
        transport.EnqueueText("""{"sessionId":"session"}""");
        // Declared before the session so the session's disposal runs first, while both events are still alive.
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var session = await MetaRealtimeStreamingSession.CreateConnectedSessionForTests(transport,
            new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "PUSH_TO_TALK", [], []));
        await transport.NextSentAsync();
        session.TranscriptReceived += _ =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler only runs while the receive loop is alive, well before the session's await-using disposal releases it.
            started.Set();
            // ReSharper disable once AccessToDisposedClosure -- released in the finally below, before the session (and then these events) are disposed.
            release.Wait(TimeSpan.FromSeconds(10));
        };
        var finalize = session.FinalizeAsync(CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"type":"transcript","transcript":"Done","final":true}""");
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(finalize.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await finalize.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Adapter_AuthenticatesAndDoesNotTreatCompletedTurnsAsTerminal()
    {
        var adapter = new MetaWebSocketAdapter(new MetaRealtimeConnectionOptions("key", MetaPlugin.DefaultTranscriptionModelId, "DIARIZATION", [], []),
            new PluginLocalization(PluginDirectory(), "de"));
        var connection = await adapter.GetConnectionOptionsAsync(CancellationToken.None);
        Assert.Equal("wss://api.meta.ai/v1/asr/realtime", connection.Uri.AbsoluteUri);
        Assert.Equal("Bearer key", connection.Headers!["Authorization"]);
        var ack = adapter.HandleMessage(WebSocketMessageType.Text, "{\"sessionId\":\"session\"}"u8.ToArray());
        Assert.Equal(WebSocketSessionSignal.Ready, ack.Signals);
        adapter.HandleMessage(WebSocketMessageType.Text, "{\"type\":\"speechStart\",\"turnId\":1}"u8.ToArray());
        adapter.HandleMessage(WebSocketMessageType.Text, "{\"type\":\"speaker\",\"label\":\"A\"}"u8.ToArray());
        var turn = adapter.HandleMessage(WebSocketMessageType.Text, "{\"type\":\"speechComplete\",\"turnId\":1,\"transcript\":\"Hallo\"}"u8.ToArray());
        Assert.Equal(WebSocketSessionSignal.None, turn.Signals);
        Assert.Equal("Sprecher A: Hallo", Assert.Single(turn.Transcripts).Text);
        Assert.NotNull(adapter.HandleMessage(WebSocketMessageType.Text, "{\"type\":\"error\",\"message\":\"failure\"}"u8.ToArray()).Fault);
        Assert.NotNull(adapter.HandleMessage(WebSocketMessageType.Text, "invalid"u8.ToArray()).Fault);
    }

    [Fact]
    public void Collector_PartialFinalAndEmptyTerminalEvents()
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");
        var partial = collector.Apply("""{"type":"transcript","transcript":"hello wor","final":false}""");
        Assert.Equal("hello wor", partial.Transcript?.Text);
        Assert.False(partial.IsFinalEvent);
        var final = collector.Apply("""{"type":"transcript","transcript":"Hello world.","final":true}""");
        Assert.Equal("Hello world.", final.Transcript?.Text);
        Assert.True(final.IsFinalEvent);
        var empty = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK").Apply("""{"type":"transcript","transcript":"","final":true}""");
        Assert.Null(empty.Transcript);
        Assert.True(empty.IsFinalEvent);
    }

    [Fact]
    public void Collector_MergesTurnsInOrderAndKeepsInterimAfterSpeechEnd()
    {
        var collector = new MetaRealtimeTranscriptCollector("DIARIZATION");
        collector.Apply("""{"type":"speechStart","turnId":2}""");
        collector.Apply("""{"type":"speaker","label":"B"}""");
        var later = collector.Apply("""{"type":"speechComplete","turnId":2,"transcript":"Second"}""");
        Assert.Null(later.Transcript);
        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply("""{"type":"speaker","label":"A"}""");
        collector.Apply("""{"type":"transcript","transcript":"Fir","final":false}""");
        var ended = collector.Apply("""{"type":"speechEnd","turnId":1}""");
        Assert.Equal("Speaker B: Second\nSpeaker A: Fir", ended.Transcript?.Text);
        var complete = collector.Apply("""{"type":"speechComplete","turnId":1,"transcript":"First"}""");
        var terminal = collector.Apply(
            """{"type":"transcript","transcript":"Speaker A: First\nSpeaker B: Second","final":true}""");
        Assert.Equal("Speaker A: First\nSpeaker B: Second",
            AccumulateFinals(later, ended, complete, terminal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collector_AccumulatesFinalsInTurnOrder(bool completeLaterTurnFirst)
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");
        collector.Apply("""{"type":"speechStart","turnId":1}""");
        collector.Apply("""{"type":"speechStart","turnId":2}""");
        const string first = """{"type":"speechComplete","turnId":1,"transcript":"First"}""";
        const string second = """{"type":"speechComplete","turnId":2,"transcript":"Second"}""";
        var early = collector.Apply(completeLaterTurnFirst ? second : first);
        if (completeLaterTurnFirst)
            Assert.Null(early.Transcript);
        var late = collector.Apply(completeLaterTurnFirst ? first : second);
        var terminal = collector.Apply(
            """{"type":"transcript","transcript":"First Second","final":true}""");
        Assert.Equal("First\nSecond", AccumulateFinals(early, late, terminal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collector_TerminalReleasesTurnsWaitingForEarlierCompletion(bool emptyTerminal)
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");
        collector.Apply("""{"type":"speechStart","turnId":1}""");
        var first = collector.Apply("""{"type":"speechComplete","turnId":1,"transcript":"First"}""");
        var third = collector.Apply("""{"type":"speechComplete","turnId":3,"transcript":"Third"}""");
        Assert.Null(third.Transcript);
        var terminal = collector.Apply(JsonSerializer.Serialize(new
        {
            type = "transcript", transcript = emptyTerminal ? "" : "First Second Third", final = true,
        }));
        Assert.Equal(emptyTerminal ? "First\nThird" : "First\nSecond Third",
            AccumulateFinals(first, third, terminal));
    }

    [Fact]
    public void Collector_FaultsWhenTerminalReordersEmittedTurns()
    {
        var collector = new MetaRealtimeTranscriptCollector("PUSH_TO_TALK");
        var second = collector.Apply("""{"type":"speechComplete","turnId":2,"transcript":"Second"}""");
        var first = collector.Apply("""{"type":"speechComplete","turnId":1,"transcript":"First"}""");
        Assert.Equal("First\nSecond", AccumulateFinals(second, first));
        Assert.Throws<InvalidOperationException>(() => collector.Apply(
            """{"type":"transcript","transcript":"Second First","final":true}"""));
    }

    private static string AccumulateFinals(params MetaRealtimeUpdate[] updates) =>
        string.Join("\n", updates.Select(update => update.Transcript)
            .Where(transcript => transcript is { IsFinal: true } && !string.IsNullOrWhiteSpace(transcript.Text))
            .Select(transcript => transcript!.Text.Trim()));

    [Theory]
    [InlineData("de", "A", "Sprecher A: Hallo")]
    [InlineData("de", "Speaker B", "Sprecher B: Hallo")]
    [InlineData("es", "A", "Hablante A: Hallo")]
    [InlineData("ru", "A", "Говорящий A: Hallo")]
    public void BatchDiarization_LocalizesSpeakerLabels(string language, string speaker, string expected)
    {
        var result = MetaPlugin.ParseTranscriptionResponse(
            JsonSerializer.Serialize(new { transcript = "Hallo", turns = new[] { new { speaker, transcript = "Hallo" } } }),
            null, true, new PluginLocalization(PluginDirectory(), language));
        Assert.Equal(expected, result.Text);
    }

    private sealed class DelayedCatalogHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StubSession : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived { add { } remove { } }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins", "TypeWhisper.Plugin.Meta"));

    private static byte[] CreatePcm16Wav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(4);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode StatusCode, string Body)> _responses = [];

        public RecordingHandler(HttpStatusCode statusCode, string responseBody)
        {
            EnqueueResponse(statusCode, responseBody);
        }

        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? Accept { get; private set; }
        public string? RequestBody { get; private set; }
        public string? MultipartRequest { get; private set; }
        public string? AudioContentType { get; private set; }
        public byte[]? Audio { get; private set; }

        public void EnqueueResponse(HttpStatusCode statusCode, string responseBody) =>
            _responses.Enqueue((statusCode, responseBody));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Accept = request.Headers.Accept.FirstOrDefault()?.MediaType;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    switch (part.Headers.ContentDisposition?.Name?.Trim('"'))
                    {
                        case "request":
                            MultipartRequest = await part.ReadAsStringAsync(cancellationToken);
                            break;
                        case "audio":
                            AudioContentType = part.Headers.ContentType?.MediaType;
                            Audio = await part.ReadAsByteArrayAsync(cancellationToken);
                            break;
                    }
                }
            }
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakePluginHostServices : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];

        public Dictionary<string, string> Secrets { get; } = [];
        public bool FailSecretWrites { get; set; }
        public string SettingsSnapshot => JsonSerializer.Serialize(_settings);
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new PluginLocalization(PluginDirectory(), "en");

        public Task StoreSecretAsync(string key, string value)
        {
            if (FailSecretWrites)
                throw new IOException("Secret storage failed.");
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            if (FailSecretWrites)
                throw new IOException("Secret storage failed.");
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;

        public void SetSetting<T>(string key, T value) => _settings[key] = JsonSerializer.SerializeToElement(value);
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

}
