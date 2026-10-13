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
    public async Task StartStreamingAsync_RejectsUnspecifiedLanguage(string? language)
    {
        // Defense in depth for direct/legacy callers; the typed host rejects this first.
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.StartStreamingAsync(language, CancellationToken.None)
        );
    }

    [Fact]
    public void BuildStartRecognition_UsesRawPcmAndEnablesPartials()
    {
        var json = SmSession.BuildStartRecognition("de", [], 16000);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("StartRecognition", root.GetProperty("message").GetString());

        var format = root.GetProperty("audio_format");
        Assert.Equal("raw", format.GetProperty("type").GetString());
        Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
        Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());

        var config = root.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.Equal("enhanced", config.GetProperty("operating_point").GetString());
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
            () => SmSession.BuildStartRecognition(language, [], 16000)
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
        Assert.Equal("transcription", root.GetProperty("type").GetString());
        var config = root.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.Equal("enhanced", config.GetProperty("operating_point").GetString());
        AssertAdditionalVocab(config, "Washington, D.C.", "TypeWhisper");
        Assert.Equal(
            ["language", "operating_point", "additional_vocab"],
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
            ["language", "operating_point"],
            config.EnumerateObject().Select(property => property.Name).ToArray()
        );
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
        var json = SmSession.BuildStartRecognition("de", ["Washington, D.C.", "TypeWhisper"], 16000);

        using var doc = JsonDocument.Parse(json);
        var config = doc.RootElement.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.True(config.GetProperty("enable_partials").GetBoolean());
        AssertAdditionalVocab(config, "Washington, D.C.", "TypeWhisper");
    }

    [Fact]
    public async Task WebSocketAdapter_StartRecognitionCarriesVocabulary()
    {
        var adapter = new SpeechmaticsWebSocketAdapter("key", "fr", ["TypeWhisper"]);

        var messages = await adapter.OnConnectedAsync(CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        using var doc = JsonDocument.Parse(message.Payload);
        var config = doc.RootElement.GetProperty("transcription_config");
        Assert.Equal("fr", config.GetProperty("language").GetString());
        AssertAdditionalVocab(config, "TypeWhisper");
    }

    [Fact]
    public async Task StartStreamingWithPrompt_RejectsHintsWithoutALanguage()
    {
        var host = new TestHost { Secrets = { ["api-key"] = "sm-key" } };
        using var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<NotSupportedException>(() => sut.StartStreamingWithLanguageHintsAndPromptAsync(
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://asr.api.speechmatics.com/v2/jobs", request.RequestUri?.ToString());
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
