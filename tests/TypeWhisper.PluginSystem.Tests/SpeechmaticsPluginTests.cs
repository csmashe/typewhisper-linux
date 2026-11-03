using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Speechmatics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using SmSession = TypeWhisper.Plugin.Speechmatics.SpeechmaticsStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public class SpeechmaticsPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<SpeechmaticsPlugin>();


    private static readonly JsonSerializerOptions s_manifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "plugins",
                "TypeWhisper.Plugin.Speechmatics",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_manifestJsonOptions
        );

        var sut = new SpeechmaticsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };

        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.speechmatics", sut.PluginId);
        Assert.Equal("speechmatics", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public async Task StartStreamingAsync_WithoutALiveLanguage_FailsAsConfigurationBeforeConnecting(string? language)
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.StartStreamingAsync(language, CancellationToken.None)
        );
        Assert.Equal(PluginRequestFailureKind.Configuration, ex.FailureKind);
        Assert.Equal("Settings.LiveLanguageRequired", ex.Message);
    }

    [Theory]
    [InlineData(null, "cmn_en")]
    [InlineData("auto", "cmn_en")]
    [InlineData(" DE ", "de")]
    [InlineData("zh", "cmn")]
    public async Task ResolveStreamingLanguage_UsesTheLiveLanguageOnlyForAutomatic(string? language, string expected)
    {
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(new TestHost());
        await sut.SetSettingValueAsync("liveLanguage", "cmn_en");

        Assert.Equal(expected, sut.ResolveStreamingLanguage(language));
    }

    [Fact]
    public void BuildStartRecognition_UsesRawPcmAndEnablesPartials()
    {
        var json = SmSession.BuildStartRecognition("de", "enhanced", [], 16000);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("StartRecognition", root.GetProperty("message").GetString());

        var format = root.GetProperty("audio_format");
        Assert.Equal("raw", format.GetProperty("type").GetString());
        Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
        Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());

        var config = root.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.Equal("enhanced", config.GetProperty("model").GetString());
        Assert.False(config.TryGetProperty("operating_point", out _));
        Assert.True(config.GetProperty("enable_partials").GetBoolean());
        Assert.False(config.TryGetProperty("additional_vocab", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public void BuildStartRecognition_RejectsUnspecifiedLanguage(string? language)
    {
        Assert.Throws<ArgumentException>(
            () => SmSession.BuildStartRecognition(language, "enhanced", [], 16000)
        );
    }

    [Fact]
    public void DeclaresStructuredDictionaryTermsWithVocabularyBudget()
    {
        using var sut = new SpeechmaticsPlugin();

        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.Equal(
            new DictionaryTermsBudget(MaxTerms: 100, MaxWordsPerTerm: 6, MaxTotalChars: 4_000),
            sut.DictionaryTermsBudget
        );
    }

    [Fact]
    public async Task TranscribeAsync_SendsDictionaryTermsWholeAsAdditionalVocab()
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.TranscribeAsync(
            [1, 2, 3],
            "DE",
            translate: false,
            PluginTranscriptionPrompt.Encode(
                "Likely spoken languages: de, en.",
                ["Washington, D.C.", "TypeWhisper"]
            ),
            CancellationToken.None
        ));

        var root = Assert.NotNull(handler.Config);
        Assert.Equal(["type", "transcription_config"], root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("transcription", root.GetProperty("type").GetString());
        var config = root.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.Equal("enhanced", config.GetProperty("model").GetString());
        AssertAdditionalVocab(config, "Washington, D.C.", "TypeWhisper");
        Assert.Equal(
            ["language", "model", "additional_vocab"],
            config.EnumerateObject().Select(property => property.Name).ToArray()
        );
    }

    [Fact]
    public async Task TranscribeAsync_PromptTextWithoutTermsSendsNoAdditionalVocab()
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => sut.TranscribeAsync(
            [1, 2, 3],
            "en",
            translate: false,
            PluginTranscriptionPrompt.Encode("Alpha, Beta", []),
            CancellationToken.None
        ));

        var config = Assert.NotNull(handler.Config).GetProperty("transcription_config");
        Assert.Equal(
            ["language", "model"],
            config.EnumerateObject().Select(property => property.Name).ToArray()
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    public async Task TranscribeAsync_AutomaticIdentifiesTheLanguageAndKeepsLowConfidenceGuesses(string? language)
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.TranscribeAsync([1, 2, 3], language, translate: false, null, CancellationToken.None));

        var root = Assert.NotNull(handler.Config);
        Assert.Equal(
            ["type", "transcription_config", "language_identification_config"],
            root.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal(
            "allow",
            root.GetProperty("language_identification_config").GetProperty("low_confidence_action").GetString()
        );
        var config = root.GetProperty("transcription_config");
        Assert.Equal("auto", config.GetProperty("language").GetString());
        Assert.Equal(
            ["language", "model"],
            config.EnumerateObject().Select(property => property.Name).ToArray()
        );
    }

    [Fact]
    public async Task TranscribeAsync_SendsChineseAsMandarin()
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.TranscribeAsync([1, 2, 3], "zh", translate: false, null, CancellationToken.None));

        Assert.Equal("cmn", handler.Config!.Value.GetProperty("transcription_config").GetProperty("language").GetString());
    }

    [Fact]
    public async Task TranscribeAsync_SendsTheSelectedStandardModel()
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);
        await sut.SetSettingValueAsync("selectedModel", "standard");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, null, CancellationToken.None));

        Assert.Equal("standard", handler.Config!.Value.GetProperty("transcription_config").GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(null, "https://eu1.asr.api.speechmatics.com/v2/jobs")]
    [InlineData("eu", "https://eu1.asr.api.speechmatics.com/v2/jobs")]
    [InlineData("us", "https://us1.asr.api.speechmatics.com/v2/jobs")]
    [InlineData("au", "https://au1.asr.api.speechmatics.com/v2/jobs")]
    [InlineData("mars", "https://eu1.asr.api.speechmatics.com/v2/jobs")]
    public async Task TranscribeAsync_SubmitsToTheSavedRegion(string? savedRegion, string expectedUrl)
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        if (savedRegion is not null)
            host.SetSetting("region", savedRegion);
        var handler = new SubmitCapturingHandler();
        using var sut = new SpeechmaticsPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, null, CancellationToken.None));

        Assert.Equal(expectedUrl, handler.SubmitUrl);
    }

    [Fact]
    public async Task RegionAndLiveLanguage_ArePersistedAndValidated()
    {
        var host = new TestHost();
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("region", "us");
        await sut.SetSettingValueAsync("liveLanguage", "de");
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("region", "mars"));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("liveLanguage", "xx"));

        Assert.Equal("us", await sut.GetSettingValueAsync("region"));
        Assert.Equal("de", await sut.GetSettingValueAsync("liveLanguage"));
        Assert.Equal("us", host.GetSetting<string>("region"));
        Assert.Equal("de", host.GetSetting<string>("liveLanguage"));

        await sut.SetSettingValueAsync("liveLanguage", "");
        Assert.Equal("", await sut.GetSettingValueAsync("liveLanguage"));
        Assert.Null(sut.LiveLanguage);
    }

    [Fact]
    public void DeclaresBatchAutomaticDetectionAndTheSpeechmaticsLanguages()
    {
        using var sut = new SpeechmaticsPlugin();

        Assert.Equal(LanguageSelectionSupport.Supported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.ExplicitSelectionSupport);
        Assert.Contains("cmn", sut.SupportedLanguages);
        Assert.Contains("zh", sut.SupportedLanguages);
        Assert.DoesNotContain(sut.SupportedLanguages, code => code.Contains('_'));
        Assert.Equal(["enhanced", "standard"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task TranscribeAsync_KeepsEveryJobRequestOnTheRegionHost()
    {
        var handler = new JobHandler(
            """{ "job": { "status": "running" } }""",
            """{ "job": { "status": "done", "duration": 3.5 } }""",
            """
            { "metadata": { "language_pack_info": { "word_delimiter": " " } },
              "results": [
                { "type": "word", "alternatives": [{ "content": "Hello", "language": "en" }] },
                { "type": "punctuation", "attaches_to": "previous", "alternatives": [{ "content": "." }] } ] }
            """
        );
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        host.SetSetting("region", "au");
        using var sut = new SpeechmaticsPlugin(new HttpClient(handler)) { PollInterval = TimeSpan.Zero };
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], null, translate: false, null, CancellationToken.None);

        Assert.Equal("Hello.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(3.5, result.DurationSeconds);
        Assert.Equal(
            [
                "POST https://au1.asr.api.speechmatics.com/v2/jobs",
                "GET https://au1.asr.api.speechmatics.com/v2/jobs/job%2F1",
                "GET https://au1.asr.api.speechmatics.com/v2/jobs/job%2F1",
                "GET https://au1.asr.api.speechmatics.com/v2/jobs/job%2F1/transcript?format=json-v2",
            ],
            handler.Requests
        );
    }

    [Fact]
    public async Task TranscribeAsync_NoSpeechRejectionIsAnEmptyTranscript()
    {
        var handler = new JobHandler(
            """{ "job": { "status": "rejected", "duration": 1.5, "errors": [{ "message": "No speech found for language identification", "timestamp": "2023-02-27T11:59:20.321Z" }] } }""");
        using var sut = new SpeechmaticsPlugin(new HttpClient(handler)) { PollInterval = TimeSpan.Zero };
        await sut.ActivateAsync(new TestHost { Secrets = { ["api-key"] = "sm-key" } });

        var result = await sut.TranscribeAsync([1, 2, 3], null, translate: false, null, CancellationToken.None);

        Assert.Equal("", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("""{ "job": { "status": "rejected", "errors": [{ "message": "The identified language 'uz' is not supported for transcription" }] } }""", "rejected: The identified language 'uz' is not supported for transcription")]
    [InlineData("""{ "job": { "status": "rejected" } }""", "rejected")]
    public async Task TranscribeAsync_OtherRejectionsCarryTheProviderReason(string status, string expectedEnding)
    {
        var handler = new JobHandler(status);
        using var sut = new SpeechmaticsPlugin(new HttpClient(handler)) { PollInterval = TimeSpan.Zero };
        await sut.ActivateAsync(new TestHost { Secrets = { ["api-key"] = "sm-key" } });

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1, 2, 3], null, translate: false, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, ex.FailureKind);
        Assert.EndsWith(expectedEnding, ex.Message);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task TranscribeAsync_FailsOnAJobThatWillNotFinish(string status)
    {
        var handler = new JobHandler($$"""{ "job": { "status": "{{status}}" } }""");
        using var sut = new SpeechmaticsPlugin(new HttpClient(handler)) { PollInterval = TimeSpan.Zero };
        await sut.ActivateAsync(new TestHost { Secrets = { ["api-key"] = "sm-key" } });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, null, CancellationToken.None));

        Assert.Contains(status, ex.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void ParseTranscript_JoinsWordsWithTheDelimiterAndAttachesPunctuation()
    {
        var result = Parse(
            """
            { "metadata": { "language_pack_info": { "word_delimiter": " " } },
              "results": [
                { "type": "word", "alternatives": [{ "content": "Hello", "language": "en" }] },
                { "type": "punctuation", "attaches_to": "previous", "alternatives": [{ "content": "," }] },
                { "type": "word", "alternatives": [{ "content": "world" }] },
                { "type": "punctuation", "alternatives": [{ "content": "." }] },
                { "type": "punctuation", "attaches_to": "next", "alternatives": [{ "content": "¿" }] },
                { "type": "word", "alternatives": [{ "content": "well" }] },
                { "type": "punctuation", "attaches_to": "both", "alternatives": [{ "content": "-" }] },
                { "type": "word", "alternatives": [{ "content": "known" }] },
                { "type": "punctuation", "attaches_to": "none", "alternatives": [{ "content": "&" }] },
                { "type": "entity", "alternatives": [{ "content": "ignored" }] },
                { "type": "word", "alternatives": [] },
                { "type": "word", "alternatives": [{ "content": "more" }] } ] }
            """,
            null
        );

        Assert.Equal("Hello, world. ¿well-known & more", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
    }

    [Fact]
    public void ParseTranscript_UsesAnEmptyDelimiterForLanguagesWithoutSpaces()
    {
        var result = Parse(
            """
            { "metadata": { "language_pack_info": { "word_delimiter": "" } },
              "results": [
                { "type": "word", "alternatives": [{ "content": "你好", "language": "cmn" }] },
                { "type": "word", "alternatives": [{ "content": "世界", "language": "cmn" }] },
                { "type": "punctuation", "attaches_to": "previous", "alternatives": [{ "content": "。" }] } ] }
            """,
            null
        );

        Assert.Equal("你好世界。", result.Text);
        Assert.Equal("zh", result.DetectedLanguage);
    }

    [Theory]
    [InlineData("""{ "metadata": { "transcription_config": { "language": "de" } }, "results": [] }""", null, "de")]
    [InlineData("""{ "metadata": { "transcription_config": { "language": "auto" } }, "results": [] }""", null, null)]
    [InlineData("""{ "metadata": { "transcription_config": { "language": "auto" } }, "results": [] }""", "fr", "fr")]
    [InlineData("""{ "results": [] }""", "fr", "fr")]
    public void ParseTranscript_FallsBackForTheDetectedLanguage(string json, string? requested, string? expected)
    {
        var result = Parse(json, requested);

        Assert.Equal("", result.Text);
        Assert.Equal(expected, result.DetectedLanguage);
    }

    [Theory]
    [InlineData("""{ "metadata": {} }""")]
    [InlineData("""{ "results": {} }""")]
    public void ParseTranscript_RejectsATranscriptWithoutResults(string json)
    {
        var ex = Assert.Throws<PluginRequestException>(() => Parse(json, "en"));

        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, ex.FailureKind);
    }

    private static PluginTranscriptionResult Parse(string json, string? requestedLanguage)
    {
        using var job = JsonDocument.Parse("""{ "status": "done" }""");
        return SpeechmaticsPlugin.ParseTranscript(json, job.RootElement, requestedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_MalformedPromptEnvelopeFailsBeforeHttp()
    {
        var handler = new SubmitCapturingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAsync<FormatException>(() => sut.TranscribeAsync(
            [1, 2, 3],
            "en",
            translate: false,
            PluginTranscriptionPrompt.EnvelopePrefix + "[]",
            CancellationToken.None
        ));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void ExtractVocabulary_ClipsTermsToTheBudget()
    {
        var terms = Enumerable.Range(1, 150).Select(index => $"Term {index}").ToList();
        terms.Insert(0, "one two three four five six seven");

        var vocabulary = SpeechmaticsPlugin.ExtractVocabulary(PluginTranscriptionPrompt.Encode(null, terms));

        Assert.Equal(terms.Skip(1).Take(100), vocabulary);
    }

    [Fact]
    public void BuildStartRecognition_SendsTermsWholeAsAdditionalVocab()
    {
        var json = SmSession.BuildStartRecognition("de", "enhanced", ["Washington, D.C.", "TypeWhisper"], 16000);

        using var doc = JsonDocument.Parse(json);
        var config = doc.RootElement.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.True(config.GetProperty("enable_partials").GetBoolean());
        AssertAdditionalVocab(config, "Washington, D.C.", "TypeWhisper");
    }

    [Fact]
    public async Task WebSocketAdapter_StartRecognitionCarriesVocabulary()
    {
        var adapter = new SpeechmaticsWebSocketAdapter(
            "key", new Uri("wss://us.rt.speechmatics.com/v2"), "fr", "standard", ["TypeWhisper"]);

        var messages = await adapter.OnConnectedAsync(CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using var doc = JsonDocument.Parse(message.Payload);
        var config = doc.RootElement.GetProperty("transcription_config");
        Assert.Equal("fr", config.GetProperty("language").GetString());
        Assert.Equal("standard", config.GetProperty("model").GetString());
        AssertAdditionalVocab(config, "TypeWhisper");
        var options = await adapter.GetConnectionOptionsAsync(CancellationToken.None);
        Assert.Equal("wss://us.rt.speechmatics.com/v2", options.Uri.ToString());
    }

    [Theory]
    [InlineData("de", "de")]
    [InlineData("cmn", "zh")]
    [InlineData("cmn_en", null)]
    public void WebSocketAdapter_FinalTranscriptReportsTheSessionLanguage(string language, string? expected)
    {
        var adapter = new SpeechmaticsWebSocketAdapter(
            "key", new Uri("wss://eu.rt.speechmatics.com/v2"), language, "enhanced", []);

        adapter.HandleMessage(WebSocketMessageType.Text,
            """{ "message": "AddTranscript", "metadata": { "transcript": "hallo" }, "results": [] }"""u8.ToArray());
        var end = adapter.HandleMessage(WebSocketMessageType.Text,
            """{ "message": "EndOfTranscript" }"""u8.ToArray());

        var final = Assert.Single(end.Transcripts);
        Assert.True(final.IsFinal);
        Assert.Equal("hallo", final.Text);
        Assert.Equal(expected, final.DetectedLanguage);
    }

    [Fact]
    public async Task StartStreamingWithPrompt_RejectsHintsWithoutALanguage()
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingWithLanguageHintsAndPromptAsync(
            [" ", "auto"],
            PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]),
            CancellationToken.None
        ));
    }

    [Fact]
    public async Task StartStreamingWithPrompt_MalformedPromptEnvelopeFailsBeforeConnecting()
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<FormatException>(() => sut.StartStreamingWithLanguageHintsAndPromptAsync(
            ["de"],
            PluginTranscriptionPrompt.EnvelopePrefix + "[]",
            CancellationToken.None
        ));
    }

    private static void AssertAdditionalVocab(JsonElement transcriptionConfig, params string[] expected)
    {
        var entries = transcriptionConfig.GetProperty("additional_vocab").EnumerateArray().ToArray();
        Assert.All(entries, entry => Assert.Equal(["content"], entry.EnumerateObject().Select(p => p.Name).ToArray()));
        Assert.Equal(expected, entries.Select(entry => entry.GetProperty("content").GetString()).ToArray());
    }

    private static async Task<SpeechmaticsPlugin> CreateConfiguredPluginAsync(HttpMessageHandler handler)
    {
        var sut = new SpeechmaticsPlugin(new HttpClient(handler));
        await sut.ActivateAsync(new TestHost { Secrets = { ["api-key"] = "sm-key" } });
        return sut;
    }

    // Captures the job config and rejects the submit, so no test waits on the poll delay.
    private sealed class SubmitCapturingHandler : HttpMessageHandler
    {
        public JsonElement? Config { get; private set; }
        public int RequestCount { get; private set; }
        public string? SubmitUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            SubmitUrl = request.RequestUri?.ToString();
            Assert.Equal("Bearer sm-key", request.Headers.Authorization?.ToString());

            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var configPart = Assert.Single(multipart, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "config");
            Config = JsonDocument.Parse(await configPart.ReadAsStringAsync(cancellationToken)).RootElement.Clone();

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    // Accepts the submit as job "job/1", then answers each later request in order.
    private sealed class JobHandler(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add($"{request.Method} {request.RequestUri?.AbsoluteUri}");
            Assert.Equal("Bearer sm-key", request.Headers.Authorization?.ToString());
            var body = request.Method == HttpMethod.Post ? """{ "id": "job/1" }""" : responses[_next++];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void BuildEndOfStream_CarriesSeqNo()
    {
        var json = SmSession.BuildEndOfStream(42);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("EndOfStream", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("last_seq_no").GetInt32());
    }

    [Fact]
    public void ParseMessage_ReadsTranscriptFromMetadata()
    {
        // Canonical real-time shape: the plain-text segment is under metadata.transcript.
        var msg = SmSession.ParseMessage(
            """
            { "message": "AddTranscript", "format": "2.1",
              "metadata": { "transcript": "hello world", "start_time": 0.0, "end_time": 1.5 },
              "results": [] }
            """
        );

        Assert.Equal("AddTranscript", msg.MessageType);
        Assert.Equal("hello world", msg.Transcript);
        Assert.Null(msg.ErrorReason);
    }

    [Fact]
    public void ParseMessage_FallsBackToRootLevelTranscript()
    {
        // Defensive fallback for a root-level "transcript" shape.
        var msg = SmSession.ParseMessage(
            """{ "message": "AddPartialTranscript", "transcript": "hi there", "metadata": {} }"""
        );

        Assert.Equal("AddPartialTranscript", msg.MessageType);
        Assert.Equal("hi there", msg.Transcript);
    }

    [Fact]
    public void ParseMessage_SurfacesErrorReason()
    {
        var msg = SmSession.ParseMessage(
            """{ "message": "Error", "type": "quota_exceeded", "reason": "limit reached", "code": 4005 }"""
        );

        Assert.Equal("Error", msg.MessageType);
        Assert.Equal("limit reached", msg.ErrorReason);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var msg = SmSession.ParseMessage("garbage {");

        Assert.Equal("", msg.MessageType);
        Assert.Null(msg.Transcript);
    }

    [Fact]
    public void Aggregator_AccumulatesFinals_AndReplacesPartialTail()
    {
        var aggregator = new SpeechmaticsTranscriptAggregator();

        var partial = aggregator.Apply(Msg("AddPartialTranscript", "hello wor"));
        Assert.Equal("hello wor", partial.PreviewText);
        Assert.False(partial.Completed);

        var final1 = aggregator.Apply(Msg("AddTranscript", "hello world"));
        Assert.Equal("hello world", final1.PreviewText);
        Assert.Equal("hello world", final1.FinalText);

        // Partial tail is replaced, not appended onto the accumulated final.
        var partial2 = aggregator.Apply(Msg("AddPartialTranscript", " how are"));
        Assert.Equal("hello world how are", partial2.PreviewText);

        var final2 = aggregator.Apply(Msg("AddTranscript", " how are you"));
        Assert.Equal("hello world how are you", final2.FinalText);
    }

    [Fact]
    public void Aggregator_AttachesASegmentThatOpensWithPunctuation()
    {
        var aggregator = new SpeechmaticsTranscriptAggregator();
        aggregator.Apply(Msg("AddTranscript", "Hello world "));

        var partial = aggregator.Apply(Msg("AddPartialTranscript", " ."));
        Assert.Equal("Hello world.", partial.PreviewText);

        aggregator.Apply(Msg("AddTranscript", " , then "));
        var final = aggregator.Apply(Msg("AddTranscript", "more"));
        Assert.Equal("Hello world, then more", final.FinalText);
    }

    [Fact]
    public void Aggregator_SignalsCompleted_OnEndOfTranscript()
    {
        var aggregator = new SpeechmaticsTranscriptAggregator();
        aggregator.Apply(Msg("AddTranscript", "all done"));
        var end = aggregator.Apply(Msg("EndOfTranscript", null));

        Assert.True(end.Completed);
        Assert.Equal("all done", end.FinalText);
    }

    private static SmSession.SpeechmaticsMessage Msg(string type, string? transcript) =>
        new(type, transcript, null);

    private sealed class TestHost : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestBus();
        public IReadOnlyList<string> AvailableProfileNames => [];

        public void Log(PluginLogLevel level, string message) { }

        public void NotifyCapabilitiesChanged() { }

        public IPluginLocalization Localization { get; } = new TestLocalization();
    }

    private sealed class TestLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent => new NoOp();
    }

    private sealed class NoOp : IDisposable
    {
        public void Dispose() { }
    }
}
