using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.ElevenLabs;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class ElevenLabsPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<ElevenLabsPlugin>();


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
                "TypeWhisper.Plugin.ElevenLabs",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_manifestJsonOptions
        );

        var sut = new ElevenLabsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_UsesScribeV2AsDefaultModel()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "eleven-key" } };

        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.elevenlabs", sut.PluginId);
        Assert.Equal("elevenlabs", sut.ProviderId);
        Assert.Equal(ElevenLabsPlugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(
            [ElevenLabsPlugin.DefaultModelId],
            sut.TranscriptionModels.Select(m => m.Id).ToArray()
        );
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.Contains("de", sut.SupportedLanguages);
    }

    [Fact]
    public async Task SetSettingValueAsync_UpdatesApiKeyAndModel()
    {
        var host = new TestPluginHostServices();
        var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("api-key", " eleven-key ");
        await sut.SetSettingValueAsync("selectedModel", ElevenLabsPlugin.DefaultModelId);

        Assert.Equal("eleven-key", host.Secrets["api-key"]);
        Assert.Equal(ElevenLabsPlugin.DefaultModelId, host.GetSetting<string>("selectedModel"));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesUserEndpointAndXiApiKeyHeader()
    {
        var handler = new CapturingHandler((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https://api.elevenlabs.io/v1/user", request.RequestUri?.ToString());
                Assert.True(request.Headers.TryGetValues("xi-api-key", out var values));
                Assert.Equal("eleven-key", Assert.Single(values));
                return JsonResponse("""{"user_id":"u"}""");
            }
        );

        using var httpClient = new HttpClient(handler);
        var sut = new ElevenLabsPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("eleven-key"));
    }

    [Fact]
    public async Task TranscribeAsync_SendsMultipartRequestAndParsesResponse()
    {
        var handler = new CapturingHandler((request, body) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://api.elevenlabs.io/v1/speech-to-text",
                    request.RequestUri?.ToString()
                );
                Assert.True(request.Headers.TryGetValues("xi-api-key", out var values));
                Assert.Equal("eleven-key", Assert.Single(values));
                Assert.StartsWith(
                    "multipart/form-data",
                    request.Content?.Headers.ContentType?.MediaType
                );

                Assert.NotNull(body);
                Assert.Contains("model_id", body);
                Assert.Contains("scribe_v2", body);
                Assert.Contains("language_code", body);
                Assert.Contains("de", body);
                Assert.Contains("keyterms", body);
                Assert.Contains("TypeWhisper", body);
                Assert.Contains("ElevenLabs", body);
                Assert.DoesNotContain("bad<term", body);

                return JsonResponse(
                    """
                    {
                      "language_code": "de",
                      "text": "Hallo Welt",
                      "words": [
                        { "text": "Hallo", "start": 0.1, "end": 0.5, "type": "word" },
                        { "text": " ", "start": 0.5, "end": 0.6, "type": "spacing" },
                        { "text": "Welt", "start": 0.6, "end": 1.0, "type": "word" }
                      ]
                    }
                    """
                );
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "eleven-key" } };

        using var httpClient = new HttpClient(handler);
        var sut = new ElevenLabsPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            false,
            "TypeWhisper, TypeWhisper, bad<term, ElevenLabs",
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.0, result.DurationSeconds);
        Assert.Equal(["Hallo", "Welt"], result.Segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void ExtractKeyterms_FiltersUnsupportedTerms()
    {
        var terms = ElevenLabsPlugin.ExtractKeyterms(
            "TypeWhisper, TypeWhisper, too many words in this single keyterm, bad<term, ElevenLabs"
        );

        Assert.Equal(["TypeWhisper", "ElevenLabs"], terms);
    }

    [Fact]
    public void BuildRealtimeUri_UsesScribeRealtimeAndVad()
    {
        var uri = ElevenLabsStreamingSession
            .BuildRealtimeUri("scribe_v2_realtime", "de", noVerbatim: true)
            .AbsoluteUri;

        Assert.StartsWith("wss://api.elevenlabs.io/v1/speech-to-text/realtime?", uri);
        Assert.Contains("model_id=scribe_v2_realtime", uri);
        Assert.Contains("audio_format=pcm_16000", uri);
        Assert.Contains("commit_strategy=vad", uri);
        Assert.Contains("include_timestamps=true", uri);
        Assert.Contains("include_language_detection=true", uri);
        Assert.Contains("no_verbatim=true", uri);
        Assert.Contains("language_code=de", uri);
    }

    [Fact]
    public void BuildAudioChunkPayload_EncodesAudioAndCommitFlag()
    {
        var json = ElevenLabsStreamingSession.BuildAudioChunkPayload([1, 2, 3], true);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("input_audio_chunk", doc.RootElement.GetProperty("message_type").GetString());
        Assert.Equal(
            Convert.ToBase64String([1, 2, 3]),
            doc.RootElement.GetProperty("audio_base_64").GetString()
        );
        Assert.Equal(16000, doc.RootElement.GetProperty("sample_rate").GetInt32());
        Assert.True(doc.RootElement.GetProperty("commit").GetBoolean());
    }

    [Fact]
    public void TryParseTranscriptEvent_ParsesPartialCommittedAndErrorMessages()
    {
        var parsedPartial = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"partial_transcript","text":"Hello"}""",
            out var partial,
            out var partialError
        );
        var parsedFinal = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"committed_transcript_with_timestamps","text":"Hello world","language_code":"en"}""",
            out var final,
            out var finalError
        );
        var parsedError = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            """{"message_type":"scribe_auth_error","message":"Invalid API key"}""",
            out var errorEvent,
            out var error
        );

        Assert.True(parsedPartial);
        Assert.Equal(new StreamingTranscriptEvent("Hello", false), partial);
        Assert.Null(partialError);

        Assert.True(parsedFinal);
        Assert.Equal(new StreamingTranscriptEvent("Hello world", true), final);
        Assert.Null(finalError);

        Assert.False(parsedError);
        Assert.Null(errorEvent);
        Assert.Equal("Invalid API key", error);
    }


    [Fact]
    public async Task TranscriptionSettings_PersistAndNotifyCapabilityChanges()
    {
        var host = new TestPluginHostServices();
        using var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("transcriptionMode", "restOnly");
        await sut.SetSettingValueAsync("tagAudioEvents", "true");
        await sut.SetSettingValueAsync("noVerbatim", "false");
        await sut.SetSettingValueAsync("numSpeakers", "33");

        Assert.Equal("restOnly", host.GetSetting<string>("transcriptionMode"));
        Assert.True(host.GetSetting<bool>("tagAudioEvents"));
        Assert.False(host.GetSetting<bool>("noVerbatim"));
        Assert.Equal(32, host.GetSetting<int>("numSpeakers"));
        Assert.False(sut.SupportsStreaming);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        await sut.SetSettingValueAsync("transcriptionMode", "restOnly");
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        using var reloaded = new ElevenLabsPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Equal(ElevenLabsTranscriptionMode.RestOnly, reloaded.TranscriptionMode);
        Assert.True(reloaded.TagAudioEvents);
        Assert.False(reloaded.NoVerbatim);
        Assert.Equal(32, reloaded.SpeakerCount);

        await sut.SetSettingValueAsync("transcriptionMode", "unknown");
        await sut.SetSettingValueAsync("numSpeakers", "invalid");
        Assert.Equal("restOnly", await sut.GetSettingValueAsync("transcriptionMode"));
        Assert.Equal("32", await sut.GetSettingValueAsync("numSpeakers"));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        await sut.SetSettingValueAsync("numSpeakers", "-1");
        Assert.Equal(0, host.GetSetting<int>("numSpeakers"));
        await sut.SetSettingValueAsync("transcriptionMode", "automatic");
        Assert.True(sut.SupportsStreaming);
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task GetSettingValueAsync_ReportsDefaultsExplicitly()
    {
        using var sut = new ElevenLabsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        Assert.Equal("automatic", await sut.GetSettingValueAsync("transcriptionMode"));
        Assert.Equal("true", await sut.GetSettingValueAsync("noVerbatim"));
        Assert.Equal("false", await sut.GetSettingValueAsync("tagAudioEvents"));
        Assert.Equal("1", await sut.GetSettingValueAsync("numSpeakers"));
    }

    [Fact]
    public async Task TranscribeAsync_AppliesCustomOptionsAndOmitsAutomaticSpeakerCount()
    {
        var bodies = new List<string>();
        using var httpClient = new HttpClient(new CapturingHandler((_, body) =>
        {
            Assert.NotNull(body);
            bodies.Add(body);
            return JsonResponse("""{"text":"Hello"}""");
        }));
        using var sut = new ElevenLabsPlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "eleven-key" } });
        await sut.SetSettingValueAsync("tagAudioEvents", "true");
        await sut.SetSettingValueAsync("noVerbatim", "false");
        await sut.SetSettingValueAsync("numSpeakers", "0");

        await sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None);
        var automaticBody = Assert.Single(bodies);
        AssertMultipartPart(automaticBody, "tag_audio_events", "true");
        AssertMultipartPart(automaticBody, "no_verbatim", "false");
        Assert.DoesNotContain("num_speakers", automaticBody);

        await sut.SetSettingValueAsync("numSpeakers", "3");
        await sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None);
        Assert.Equal(2, bodies.Count);
        AssertMultipartPart(bodies[1], "num_speakers", "3");
    }

    [Fact]
    public async Task TranscribeAsync_DefaultsCleanUpTranscriptAndSkipAudioEvents()
    {
        using var httpClient = new HttpClient(new CapturingHandler((_, body) =>
        {
            Assert.NotNull(body);
            AssertMultipartPart(body, "no_verbatim", "true");
            AssertMultipartPart(body, "tag_audio_events", "false");
            AssertMultipartPart(body, "num_speakers", "1");
            return JsonResponse("""{"text":"Hello"}""");
        }));
        using var sut = new ElevenLabsPlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "eleven-key" } });

        await sut.TranscribeAsync([1, 2, 3], null, false, null, CancellationToken.None);
    }

    [Fact]
    public void BuildRealtimeUri_CarriesNoVerbatimFalse()
    {
        var uri = ElevenLabsStreamingSession.BuildRealtimeUri("scribe_v2_realtime", null, noVerbatim: false);
        Assert.Contains("no_verbatim=false", uri.AbsoluteUri);
        Assert.DoesNotContain("language_code=", uri.AbsoluteUri);
    }

    [Fact]
    public void ExtractKeyterms_SplitsOnNewlinesAndCountsAnyWhitespace()
    {
        Assert.Equal(
            ["Alpha", "Beta Gamma", "Delta", "Eps\tZeta"],
            ElevenLabsPlugin.ExtractKeyterms("Alpha\r\nBeta Gamma\nDelta,Eps\tZeta")
        );
        Assert.Empty(ElevenLabsPlugin.ExtractKeyterms("one two three four five six"));
        Assert.Empty(ElevenLabsPlugin.ExtractKeyterms("one\ttwo\tthree\tfour\tfive\tsix"));
    }

    [Fact]
    public void ExtractKeyterms_LimitsRequestToOneThousandTerms()
    {
        var terms = ElevenLabsPlugin.ExtractKeyterms(
            string.Join(",", Enumerable.Range(0, 1005).Select(i => $"term{i}"))
        );
        Assert.Equal(1000, terms.Count);
        Assert.Equal("term0", terms[0]);
        Assert.Equal("term999", terms[^1]);
    }

    [Fact]
    public void SettingsLocalization_AllLocalesExposeTheSameKeys()
    {
        var english = LoadLocalization("en");
        var keys = english.EnumerateObject().Select(p => p.Name).Order().ToArray();
        using var sut = new ElevenLabsPlugin();
        foreach (var definition in sut.GetSettingDefinitions())
        {
            Assert.Contains(definition.Label, keys);
            if (definition.Description is { } description)
                Assert.Contains(description, keys);
            foreach (var option in definition.Options ?? [])
                if (option.Label.StartsWith("Settings.", StringComparison.Ordinal))
                    Assert.Contains(option.Label, keys);
        }
        foreach (var locale in new[] { "de", "es", "ru" })
        {
            var localized = LoadLocalization(locale);
            Assert.Equal(keys, localized.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.NotEqual(
                english.GetProperty("Settings.TranscriptionMode").GetString(),
                localized.GetProperty("Settings.TranscriptionMode").GetString()
            );
        }
    }

    private static void AssertMultipartPart(string body, string name, string value) =>
        Assert.Contains($"Content-Disposition: form-data; name={name}\r\n\r\n{value}\r\n", body);

    private static JsonElement LoadLocalization(string language)
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeLocalizationPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.ElevenLabs", "Localization", $"{language}.json");
        var localizationPath = Path.GetFullPath(relativeLocalizationPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(localizationPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key)
        {
            return Task.FromResult(Secrets.GetValueOrDefault(key));
        }

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key)
        {
            return _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;
        }

        public void SetSetting<T>(string key, T value)
        {
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);
        }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];

        public void Log(PluginLogLevel level, string message) { }

        public void NotifyCapabilitiesChanged()
        {
            NotifyCapabilitiesChangedCount++;
        }

        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key)
        {
            return key;
        }

        public string GetString(string key, params object[] args)
        {
            return string.Format(key, args);
        }
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent
        {
            return new NoOpDisposable();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}