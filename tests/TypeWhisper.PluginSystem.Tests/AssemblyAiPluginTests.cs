using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AssemblyAiPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<AssemblyAiPlugin>();

    [Theory]
    [InlineData(true, """["Washington, D.C.","TypeWhisper"]""")]
    [InlineData(false, null)]
    public async Task TranscribeAsync_SendsDictionaryTermsWholeOnlyWhenEnabled(bool enabled, string? expectedKeyterms)
    {
        string? submitBody = null;
        using var httpClient = new HttpClient(new CapturingHandler((request, body) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v2/upload":
                    return Json("""{"upload_url":"https://cdn.example/audio"}""");
                case "/v2/transcript":
                    submitBody = body;
                    return Json("""{"id":"t1"}""");
                default:
                    return Json("""{"status":"completed","text":"ok","audio_duration":1.5,"language_code":"de"}""");
            }
        }));
        using var sut = new AssemblyAiPlugin(httpClient);
        await sut.ActivateAsync(CreateHost(new Dictionary<string, object?>()).Object);
        await sut.SetSettingValueAsync("useDictionaryTerms", enabled ? "true" : "false");

        var prompt = PluginTranscriptionPrompt.Encode("Prompt text", ["Washington, D.C.", "TypeWhisper"]);
        var result = await sut.TranscribeAsync([1, 2, 3], "de", false, prompt, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        using var body = JsonDocument.Parse(submitBody!);
        var root = body.RootElement;
        Assert.Equal("https://cdn.example/audio", root.GetProperty("audio_url").GetString());
        Assert.Equal("""["universal-3-5-pro"]""", root.GetProperty("speech_models").GetRawText());
        Assert.Equal("de", root.GetProperty("language_code").GetString());
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.Equal(
            expectedKeyterms,
            root.TryGetProperty("keyterms_prompt", out var keyterms) ? keyterms.GetRawText() : null);
    }

    [Fact]
    public async Task TranscribeAsync_KeepsTheModelChosenAtStartWhenSettingsChangeDuringUpload()
    {
        string? submitBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v2/upload":
                    return Json("""{"upload_url":"https://cdn.example/audio"}""");
                case "/v2/transcript":
                    submitBody = body;
                    return Json("""{"id":"t1"}""");
                default:
                    return Json("""{"status":"completed","text":"ok"}""");
            }
        });
        using var httpClient = new HttpClient(handler);
        using var sut = new AssemblyAiPlugin(httpClient);
        // ReSharper disable once AccessToDisposedClosure -- runs during TranscribeAsync, before sut is disposed.
        handler.OnUpload = () => sut.SelectModel("universal-2");
        await sut.ActivateAsync(CreateHost(new Dictionary<string, object?>()).Object);
        await sut.SetSettingValueAsync("useDictionaryTerms", "true");

        var prompt = PluginTranscriptionPrompt.Encode(null, Enumerable.Range(0, 300).Select(i => $"Term{i}"));
        await sut.TranscribeAsync([1, 2, 3], null, false, prompt, CancellationToken.None);

        using var body = JsonDocument.Parse(submitBody!);
        Assert.Equal("""["universal-3-5-pro"]""", body.RootElement.GetProperty("speech_models").GetRawText());
        Assert.Equal(300, body.RootElement.GetProperty("keyterms_prompt").GetArrayLength());
    }

    [Fact]
    public void CreateSubmitBody_OmitsKeytermsWhenThereAreNone()
    {
        var body = AssemblyAiPlugin.CreateSubmitBody(
            new AssemblyAiPlugin.SubmitOptions("https://cdn.example/audio", "universal-2", null, []));

        Assert.True((bool)body["language_detection"]);
        Assert.False(body.ContainsKey("keyterms_prompt"));
        Assert.False(body.ContainsKey("speaker_labels"));
    }

    [Fact]
    public void KeytermsFor_IgnoresPromptTextAndLegacyCommaPrompts()
    {
        Assert.Empty(AssemblyAiPlugin.KeytermsFor("Acme, Beta", true, AssemblyAiPlugin.BudgetFor("universal-3-5-pro")));
        Assert.Equal(
            ["Acme"],
            AssemblyAiPlugin.KeytermsFor(
                PluginTranscriptionPrompt.Encode("Beta, Gamma", ["Acme"]),
                true,
                AssemblyAiPlugin.BudgetFor("universal-3-5-pro")));
    }

    [Fact]
    public void KeytermsFor_DropsPhrasesOverSixWordsAndCapsTotalWordsPerModel()
    {
        var terms = Enumerable.Range(0, 600).Select(i => $"alpha{i} beta{i}").Prepend("one two three four five six seven");
        var prompt = PluginTranscriptionPrompt.Encode(null, terms);

        var pro = AssemblyAiPlugin.KeytermsFor(prompt, true, AssemblyAiPlugin.BudgetFor("universal-3-5-pro"));
        var universal2 = AssemblyAiPlugin.KeytermsFor(prompt, true, AssemblyAiPlugin.BudgetFor("universal-2"));

        Assert.Equal(500, pro.Count);
        Assert.Equal("alpha0 beta0", pro[0]);
        Assert.Equal(100, universal2.Count);
        Assert.Equal("alpha99 beta99", universal2[^1]);
    }

    [Fact]
    public void DictionaryTermsBudget_FollowsSelectedModel()
    {
        using var sut = new AssemblyAiPlugin();

        Assert.Equal(1000, sut.DictionaryTermsBudget.MaxTerms);
        sut.SelectModel("universal-2");
        Assert.Equal(200, sut.DictionaryTermsBudget.MaxTerms);
        Assert.Equal(6, sut.DictionaryTermsBudget.MaxWordsPerTerm);
    }

    [Fact]
    public void KeytermsFor_RejectsMalformedEnvelope()
    {
        Assert.Throws<FormatException>(() =>
            AssemblyAiPlugin.KeytermsFor(
                PluginTranscriptionPrompt.EnvelopePrefix + "{\"text\":\"x\"}",
                true,
                AssemblyAiPlugin.BudgetFor(null)));
    }

    [Fact]
    public async Task StreamingUri_CarriesKeytermsAsJsonArray()
    {
        var keyterms = AssemblyAiPlugin.StreamingKeytermsFor(
            PluginTranscriptionPrompt.Encode(null, ["Washington, D.C.", "Zoë & Co", new string('x', 51)]),
            true);
        var options = await new AssemblyAiWebSocketAdapter("key", "de", keyterms, "universal-2")
            .GetConnectionOptionsAsync(CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(options.Uri.Query);
        Assert.Equal("universal-streaming-multilingual", query["speech_model"]);
        var keytermsPrompt = JsonSerializer.Deserialize<string[]>(query["keyterms_prompt"]!);
        Assert.NotNull(keytermsPrompt);
        Assert.Equal(["Washington, D.C.", "Zoë & Co"], keytermsPrompt);
    }

    [Fact]
    public async Task StreamingUri_OmitsKeytermsWhenDisabledOrEmpty()
    {
        var prompt = PluginTranscriptionPrompt.Encode(null, ["Acme"]);
        var options = await new AssemblyAiWebSocketAdapter("key", "en", AssemblyAiPlugin.StreamingKeytermsFor(prompt, false))
            .GetConnectionOptionsAsync(CancellationToken.None);

        Assert.DoesNotContain("keyterms_prompt", options.Uri.Query);
    }

    [Fact]
    public void StreamingKeytermsFor_LimitsCountAndEncodedLength()
    {
        var shortTerms = PluginTranscriptionPrompt.Encode(null, Enumerable.Range(0, 150).Select(i => $"Term {i}"));
        Assert.Equal(100, AssemblyAiPlugin.StreamingKeytermsFor(shortTerms, true).Count);

        // Each 50-character non-ASCII term escapes to several hundred URL characters.
        var wideTerms = PluginTranscriptionPrompt.Encode(
            null,
            Enumerable.Range(0, 100).Select(i => $"{i:D3}" + new string('ü', 47)));
        var limited = AssemblyAiPlugin.StreamingKeytermsFor(wideTerms, true);

        var next = $"{limited.Count:D3}" + new string('ü', 47);
        Assert.InRange(limited.Count, 1, 99);
        Assert.Equal("000", limited[0][..3]);
        Assert.True(EncodedLength(limited) <= AssemblyAiPlugin.MaxStreamingKeytermsQueryLength);
        Assert.True(EncodedLength([.. limited, next]) > AssemblyAiPlugin.MaxStreamingKeytermsQueryLength);
    }

    [Fact]
    public async Task UseDictionaryTerms_DefaultsOffAndPersists()
    {
        var settings = new Dictionary<string, object?>();
        using var sut = new AssemblyAiPlugin();
        await sut.ActivateAsync(CreateHost(settings).Object);

        Assert.False(sut.SupportsStructuredDictionaryTerms);
        Assert.Equal("false", await sut.GetSettingValueAsync("useDictionaryTerms"));

        await sut.SetSettingValueAsync("useDictionaryTerms", "true");

        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.Equal(true, settings["useDictionaryTerms"]);

        using var reloaded = new AssemblyAiPlugin();
        await reloaded.ActivateAsync(CreateHost(settings).Object);
        Assert.True(reloaded.SupportsStructuredDictionaryTerms);
    }

    [Fact]
    public void Catalog_ListsUniversal35ProFirstAndRecommended()
    {
        using var sut = new AssemblyAiPlugin();

        Assert.Equal(["universal-3-5-pro", "universal-2"], sut.TranscriptionModels.Select(model => model.Id));
        Assert.True(sut.TranscriptionModels[0].IsRecommended);
        Assert.Equal(18, sut.TranscriptionModels[0].LanguageCount);
        Assert.Equal("universal-3-5-pro", sut.SelectedModelId);
    }

    [Theory]
    [InlineData("universal-3-pro", "universal-3-5-pro")]
    [InlineData("retired-model", "universal-3-5-pro")]
    [InlineData("universal-2", "universal-2")]
    public async Task ActivateAsync_MovesRetiredOrUnknownSavedModelsToTheDefault(string saved, string expected)
    {
        var settings = new Dictionary<string, object?> { ["selectedModel"] = saved };
        using var sut = new AssemblyAiPlugin();

        await sut.ActivateAsync(CreateHost(settings).Object);

        Assert.Equal(expected, sut.SelectedModelId);
        Assert.Equal(expected, settings["selectedModel"]);
    }

    [Fact]
    public void SelectModel_AcceptsTheRetiredProIdAndRejectsUnknownIds()
    {
        using var sut = new AssemblyAiPlugin();
        sut.SelectModel("universal-2");

        sut.SelectModel("universal-3-pro");

        Assert.Equal("universal-3-5-pro", sut.SelectedModelId);
        Assert.Throws<ArgumentException>(() => sut.SelectModel("universal-3"));
    }

    [Fact]
    public void SupportedLanguages_FollowSelectedModel()
    {
        using var sut = new AssemblyAiPlugin();

        Assert.Contains("ja", sut.SupportedLanguages);
        Assert.DoesNotContain("ru", sut.SupportedLanguages);
        sut.SelectModel("universal-2");
        Assert.Contains("ru", sut.SupportedLanguages);
    }

    [Theory]
    [InlineData("universal-3-5-pro", null, "universal-3-6-pro", null, false)]
    [InlineData("universal-3-5-pro", "en", "universal-3-6-pro", """["en"]""", false)]
    [InlineData("universal-3-5-pro", "ja", "universal-3-6-pro", """["ja"]""", false)]
    [InlineData("universal-2", "en", "universal-streaming-english", null, true)]
    [InlineData("universal-2", "de", "universal-streaming-multilingual", null, true)]
    [InlineData("universal-2", null, "universal-streaming-multilingual", null, true)]
    public async Task StreamingUri_NamesTheModelForTheSelection(
        string modelId,
        string? language,
        string expectedSpeechModel,
        string? expectedLanguageCodes,
        bool expectFormatTurns)
    {
        var options = await new AssemblyAiWebSocketAdapter("key", language, null, modelId)
            .GetConnectionOptionsAsync(CancellationToken.None);

        var query = System.Web.HttpUtility.ParseQueryString(options.Uri.Query);
        Assert.Equal(expectedSpeechModel, query["speech_model"]);
        Assert.Equal("pcm_s16le", query["encoding"]);
        Assert.Equal(expectedLanguageCodes, query["language_codes"]);
        Assert.Equal(expectFormatTurns ? "true" : null, query["format_turns"]);
        Assert.Equal(
            expectedSpeechModel == "universal-streaming-english" ? null : "true",
            query["language_detection"]);
    }

    [Fact]
    public async Task StartStreaming_RejectsUniversal2AutoAndLanguagesOutsideUniversalStreamingBeforeConnecting()
    {
        using var sut = new AssemblyAiPlugin();
        await sut.ActivateAsync(CreateHost(new Dictionary<string, object?> { ["selectedModel"] = "universal-2" }).Object);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.StartStreamingWithLanguageHintsAndPromptAsync(["ru"], null, CancellationToken.None));
        // Automatic detection could hear any of Universal-2's languages.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.StartStreamingWithLanguageHintsAndPromptAsync([], null, CancellationToken.None));
    }

    [Fact]
    public async Task SpeakerLabels_DefaultOffAndDisableStreamingWhenOn()
    {
        var settings = new Dictionary<string, object?>();
        var host = CreateHost(settings);
        using var sut = new AssemblyAiPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.True(sut.SupportsStreaming);
        Assert.Equal("false", await sut.GetSettingValueAsync("speakerLabels"));

        await sut.SetSettingValueAsync("speakerLabels", "true");

        Assert.False(sut.SupportsStreaming);
        Assert.Equal(true, settings["speakerLabels"]);
        host.Verify(h => h.NotifyCapabilitiesChanged(), Times.Once);

        using var reloaded = new AssemblyAiPlugin();
        await reloaded.ActivateAsync(CreateHost(settings).Object);
        Assert.False(reloaded.SupportsStreaming);
    }

    [Fact]
    public async Task TranscribeAsync_WithSpeakerLabelsReturnsLabelledUtterances()
    {
        string? submitBody = null;
        using var httpClient = new HttpClient(new CapturingHandler((request, body) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v2/upload":
                    return Json("""{"upload_url":"https://cdn.example/audio"}""");
                case "/v2/transcript":
                    submitBody = body;
                    return Json("""{"id":"t1"}""");
                default:
                    return Json("""
                        {"status":"completed","text":"Hello there. Hi.","audio_duration":3,
                         "utterances":[
                           {"speaker":"A","text":"Hello there.","start":0,"end":1500},
                           {"speaker":"B","text":"Hi.","start":1600,"end":3400},
                           {"speaker":"C","text":"","start":3400,"end":3500}]}
                        """);
            }
        }));
        using var sut = new AssemblyAiPlugin(httpClient);
        await sut.ActivateAsync(CreateHost(new Dictionary<string, object?>()).Object);
        await sut.SetSettingValueAsync("speakerLabels", "true");

        var result = await sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None);

        using var body = JsonDocument.Parse(submitBody!);
        Assert.True(body.RootElement.GetProperty("speaker_labels").GetBoolean());
        Assert.Equal("Speaker A: Hello there.\nSpeaker B: Hi.", result.Text);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(1.6, result.Segments[1].Start, 3);
        Assert.Equal(3.4, result.DurationSeconds, 3);
    }

    [Fact]
    public void ParseCompleted_KeepsThePlainTextWhenAnUtteranceHasInvalidTiming()
    {
        using var doc = JsonDocument.Parse("""
            {"status":"completed","text":"Hello there. Hi.","utterances":[
              {"speaker":"A","text":"Hello there.","start":0,"end":1500},
              {"speaker":"B","text":"Hi.","start":3400,"end":1600}]}
            """);

        var result = AssemblyAiPlugin.ParseCompleted(doc.RootElement, true);

        Assert.Equal("Hello there. Hi.", result.Text);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void ParseCompleted_AcceptsNullTextAndRejectsMissingText()
    {
        using var silent = JsonDocument.Parse("""{"status":"completed","text":null}""");
        using var missing = JsonDocument.Parse("""{"status":"completed"}""");

        Assert.Equal("", AssemblyAiPlugin.ParseCompleted(silent.RootElement, false).Text);
        var ex = Assert.Throws<PluginRequestException>(() => AssemblyAiPlugin.ParseCompleted(missing.RootElement, false));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, ex.FailureKind);
    }

    [Theory]
    [InlineData("""{"upload_url":"http://cdn.example/audio"}""", null, PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""["https://cdn.example/audio"]""", null, PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"upload_url":"https://cdn.example/audio"}""", """{"status":"paused"}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"upload_url":"https://cdn.example/audio"}""", """{"status":"error","error":"Audio too short"}""", PluginRequestFailureKind.InvalidRequest)]
    public async Task TranscribeAsync_RejectsInvalidProviderResponses(
        string uploadResponse,
        string? pollResponse,
        PluginRequestFailureKind expected)
    {
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/v2/upload" => Json(uploadResponse),
                "/v2/transcript" => Json("""{"id":"t1"}"""),
                _ => Json(pollResponse!),
            }));
        using var sut = new AssemblyAiPlugin(httpClient);
        await sut.ActivateAsync(CreateHost(new Dictionary<string, object?>()).Object);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None));

        Assert.Equal(expected, ex.FailureKind);
        if (expected == PluginRequestFailureKind.InvalidRequest)
            Assert.Contains("Audio too short", ex.Message);
    }

    [Fact]
    public void StreamingTurns_SkipARedeliveredFinalAndReportTheDetectedLanguage()
    {
        var adapter = new AssemblyAiWebSocketAdapter("key", null);

        var partial = Handle(adapter, """{"type":"Turn","turn_order":0,"transcript":"hola","end_of_turn":false,"turn_is_formatted":false}""");
        var final = Handle(adapter, """{"type":"Turn","turn_order":0,"transcript":"Hola.","end_of_turn":true,"turn_is_formatted":true,"language_code":"es"}""");
        var repeated = Handle(adapter, """{"type":"Turn","turn_order":0,"transcript":"Hola.","end_of_turn":true,"turn_is_formatted":true,"language_code":"es"}""");
        var next = Handle(adapter, """{"type":"Turn","turn_order":1,"transcript":"Hola.","end_of_turn":true,"turn_is_formatted":true}""");
        var termination = Handle(adapter, """{"type":"Termination"}""");

        Assert.False(Assert.Single(partial.Transcripts).IsFinal);
        var finalEvent = Assert.Single(final.Transcripts);
        Assert.True(finalEvent.IsFinal);
        Assert.Equal("es", finalEvent.DetectedLanguage);
        Assert.Empty(repeated.Transcripts);
        Assert.True(Assert.Single(next.Transcripts).IsFinal);
        Assert.Null(termination.Fault);
        Assert.Equal(WebSocketSessionSignal.Terminal, termination.Signals);
    }

    [Fact]
    public void StreamingTurns_FaultWhenUnconfirmedTextWouldBeLost()
    {
        var terminated = new AssemblyAiWebSocketAdapter("key", null);
        Handle(terminated, """{"type":"Turn","turn_order":0,"transcript":"hello","end_of_turn":false,"turn_is_formatted":false}""");
        Assert.NotNull(Handle(terminated, """{"type":"Termination"}""").Fault);

        var skipped = new AssemblyAiWebSocketAdapter("key", null);
        Handle(skipped, """{"type":"Turn","turn_order":0,"transcript":"hello","end_of_turn":true,"turn_is_formatted":false}""");
        Assert.NotNull(Handle(skipped, """{"type":"Turn","turn_order":1,"transcript":"next","end_of_turn":false,"turn_is_formatted":false}""").Fault);

        var reordered = new AssemblyAiWebSocketAdapter("key", null);
        Handle(reordered, """{"type":"Turn","turn_order":2,"transcript":"Two.","end_of_turn":true,"turn_is_formatted":true}""");
        Assert.NotNull(Handle(reordered, """{"type":"Turn","turn_order":1,"transcript":"One.","end_of_turn":true,"turn_is_formatted":true}""").Fault);

        var missingOrder = new AssemblyAiWebSocketAdapter("key", null);
        Assert.NotNull(Handle(missingOrder, """{"type":"Turn","transcript":"x","end_of_turn":true,"turn_is_formatted":true}""").Fault);
    }

    [Fact]
    public void StreamingTurns_AcceptAnEmptyUnfinishedClosingTurn()
    {
        var adapter = new AssemblyAiWebSocketAdapter("key", "en", null, "universal-2");
        Handle(adapter, """{"type":"Turn","turn_order":0,"transcript":"Done.","end_of_turn":true,"turn_is_formatted":true}""");
        var closing = Handle(adapter, """{"type":"Turn","turn_order":1,"transcript":"","end_of_turn":false,"turn_is_formatted":false}""");

        Assert.Empty(closing.Transcripts);
        Assert.Null(Handle(adapter, """{"type":"Termination"}""").Fault);
    }

    [Fact]
    public void Localization_DefinesDictionaryTermSettingInEveryLanguage()
    {
        var directory = Path.GetFullPath(
            Path.Join("..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.AssemblyAi", "Localization"),
            AppContext.BaseDirectory);
        foreach (var language in new[] { "en", "de", "es", "ru" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, $"{language}.json")));
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Settings.UseDictionaryTerms").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Settings.UseDictionaryTermsDescription").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Settings.SpeakerLabels").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Settings.SpeakerLabelsDescription").GetString()));
        }
    }

    private static Mock<IPluginHostServices> CreateHost(Dictionary<string, object?> settings)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.LoadSecretAsync("api-key")).ReturnsAsync("aai-key");
        host.Setup(h => h.GetSetting<string>(It.IsAny<string>()))
            .Returns((string key) => settings.GetValueOrDefault(key) as string);
        host.Setup(h => h.GetSetting<bool?>(It.IsAny<string>()))
            .Returns((string key) => settings.GetValueOrDefault(key) as bool?);
        host.Setup(h => h.SetSetting(It.IsAny<string>(), It.IsAny<It.IsAnyType>()))
            .Callback(new InvocationAction(invocation =>
                settings[(string)invocation.Arguments[0]] = invocation.Arguments[1]));
        return host;
    }

    private static WebSocketInboundResult Handle(AssemblyAiWebSocketAdapter adapter, string json) =>
        adapter.HandleMessage(System.Net.WebSockets.WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json));

    private static int EncodedLength(IReadOnlyList<string> keyterms) =>
        Uri.EscapeDataString(JsonSerializer.Serialize(keyterms)).Length;

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class CapturingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public Action? OnUpload { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v2/upload")
                OnUpload?.Invoke();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
