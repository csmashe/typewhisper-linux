using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.SmallestAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public partial class SmallestAiPluginTests
{
    private const string StandardVoices = """
        { "voices": [
            { "voiceId": "magnus", "displayName": "Magnus", "tags": { "language": ["english"] } },
            { "voiceId": "devansh", "displayName": "Devansh", "tags": { "language": ["hindi", "english"] } },
            { "voiceId": " ", "displayName": "Blank" },
            "not-an-object"
        ] }
        """;

    private const string ProVoices = """
        { "voices": [
            { "voiceId": "ben", "displayName": "Ben", "tags": { "language": ["German"] } },
            { "voiceId": "magnus", "displayName": "Magnus", "tags": {} }
        ] }
        """;

    [Fact]
    public async Task SpeakAsync_PostsWavRequestForDefaultVoiceAndPlaysItsPcm()
    {
        JsonElement? sent = null;
        var samples = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.smallest.ai/waves/v1/tts", request.RequestUri?.ToString());
            Assert.Equal("Bearer smallest-key", request.Headers.Authorization?.ToString());
            Assert.Equal("audio/wav", Assert.Single(request.Headers.Accept).MediaType);
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return WavResponse(Wav(samples, sampleRate: 24_000, channels: 2));
        });
        var (sut, playback, _) = await CreateSpeakingPluginAsync(handler);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("  Hallo Welt  ", "de-DE"), CancellationToken.None);

        Assert.Same(playback.Session, session);
        Assert.Equal("Hallo Welt", sent!.Value.GetProperty("text").GetString());
        Assert.Equal("magnus", sent.Value.GetProperty("voice_id").GetString());
        Assert.Equal("lightning_v3.1", sent.Value.GetProperty("model").GetString());
        Assert.Equal("wav", sent.Value.GetProperty("output_format").GetString());
        Assert.Equal(24_000, sent.Value.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, sent.Value.GetProperty("speed").GetDouble());
        Assert.Equal("de", sent.Value.GetProperty("language").GetString());

        var played = Assert.Single(playback.Requests);
        Assert.Equal(samples, played.Payload.ToArray());
        Assert.Equal(24_000, played.SampleRate);
        Assert.Equal(2, played.Channels);
        Assert.Equal(PcmSampleFormat.Signed16LittleEndian, played.Format);
    }

    [Fact]
    public async Task SpeakAsync_SendsSelectedProVoiceWithItsModelSpeedAndProOnlyLanguage()
    {
        var bodies = new List<JsonElement>();
        var handler = new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(request.RequestUri!.AbsolutePath.Contains("-pro") ? ProVoices : StandardVoices);

            bodies.Add(JsonDocument.Parse(body!).RootElement.Clone());
            return WavResponse(Wav([1, 0]));
        });
        var (sut, _, _) = await CreateSpeakingPluginAsync(handler);
        Assert.True((await sut.RefreshVoicesAsync(CancellationToken.None)).IsSuccess);

        sut.SelectVoice("lightning_v3.1_pro:ben");
        await sut.SetSettingValueAsync("speechSpeed", "1.5");
        await sut.SpeakAsync(new TtsSpeakRequest("こんにちは", "ja"), CancellationToken.None);
        sut.SelectVoice("lightning_v3.1:magnus");
        await sut.SpeakAsync(new TtsSpeakRequest("こんにちは", "ja"), CancellationToken.None);

        Assert.Equal("ben", bodies[0].GetProperty("voice_id").GetString());
        Assert.Equal("lightning_v3.1_pro", bodies[0].GetProperty("model").GetString());
        Assert.Equal(1.5, bodies[0].GetProperty("speed").GetDouble());
        Assert.Equal("ja", bodies[0].GetProperty("language").GetString());
        // Standard voices don't speak Japanese; the API detects instead of reading it as English.
        Assert.Equal("lightning_v3.1", bodies[1].GetProperty("model").GetString());
        Assert.Equal("auto", bodies[1].GetProperty("language").GetString());
    }

    [Theory]
    [InlineData(null, "lightning_v3.1", "auto")]
    [InlineData("auto", "lightning_v3.1", "auto")]
    [InlineData("pt_BR", "lightning_v3.1", "pt")]
    [InlineData("zh-Hans", "lightning_v3.1", "auto")]
    [InlineData("zh-Hans", "lightning_v3.1_pro", "zh")]
    [InlineData("xx", "lightning_v3.1_pro", "auto")]
    public void SpeechLanguage_SendsOnlyCodesTheVoiceModelSupports(string? language, string model, string expected) =>
        Assert.Equal(expected, SmallestAiPlugin.SpeechLanguage(language, model));

    [Fact]
    public async Task SpeakAsync_RejectsOverlongTextWithoutARequest()
    {
        var handler = new CapturingHandler((_, _) => throw new InvalidOperationException("No request expected."));
        var (sut, _, _) = await CreateSpeakingPluginAsync(handler);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest(new string('a', SmallestAiPlugin.MaximumSpeechTextLength + 1)), CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, ex.FailureKind);
        var empty = await sut.SpeakAsync(new TtsSpeakRequest("   "), CancellationToken.None);
        Assert.False(empty.IsActive);
    }

    [Fact]
    public async Task SpeakAsync_RefusesTextThatWouldSpeakLongerThanTwoMinutesAtTheCurrentSpeed()
    {
        var requests = 0;
        var (sut, _, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) =>
        {
            requests++;
            return WavResponse(Wav([1, 0]));
        }));

        // 1,800 Latin characters is about two minutes at 1x: allowed at 1x, refused at 0.5x.
        var latin = new string('a', 1_800);
        await sut.SpeakAsync(new TtsSpeakRequest(latin), CancellationToken.None);
        await sut.SetSettingValueAsync("speechSpeed", "0.5");
        var slow = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest(latin), CancellationToken.None));

        // Japanese speaks about three times slower per character.
        await sut.SetSettingValueAsync("speechSpeed", "1");
        var japanese = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest(new string('あ', 700)), CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, slow.FailureKind);
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, japanese.FailureKind);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("hello", 1, 5.0 / 15)]
    [InlineData("hello", 2, 5.0 / 30)]
    [InlineData("日本語", 1, 9.0 / 15)]
    [InlineData("한국어", 0.5, 9.0 / 7.5)]
    public void EstimateSpeechDuration_WeighsDenseScriptsAndSpeed(string text, double speed, double seconds) =>
        Assert.Equal(seconds, SmallestAiPlugin.EstimateSpeechDuration(text, speed).TotalSeconds, 6);

    [Fact]
    public async Task SpeakAsync_SkipsThePaidRequestWhenNoPlayerIsAvailable()
    {
        var handler = new CapturingHandler((_, _) => throw new InvalidOperationException("No request expected."));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "smallest-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SmallestAiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None);

        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task SpeakAsync_RequiresApiKey()
    {
        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(new TestPluginHostServices { PcmPlayback = new RecordingPcmPlayback() });

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Configuration, ex.FailureKind);
    }

    [Fact]
    public async Task SpeakAsync_RejectsOversizedAudioByLengthHeaderOrBodyWithoutPlaying()
    {
        var declared = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        declared.Content.Headers.ContentLength = SmallestAiPlugin.MaximumSpeechAudioBytes + 1L;
        var responses = new Queue<HttpResponseMessage>(
        [
            declared,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnknownLengthStream(SmallestAiPlugin.MaximumSpeechAudioBytes + 1)),
            },
        ]);
        var (sut, playback, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) => responses.Dequeue()));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
                sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None));
            Assert.Equal(PluginRequestFailureKind.RequestTooLarge, ex.FailureKind);
            Assert.Equal("Speech.AudioTooLarge", ex.Message);
        }

        Assert.Empty(playback.Requests);
    }

    [Fact]
    public async Task SpeakAsync_RejectsAudioLongerThanTwoMinutes()
    {
        // Two minutes and one sample of 8 kHz mono.
        var samples = new byte[(8_000 * 120 + 1) * 2];
        var (sut, playback, _) = await CreateSpeakingPluginAsync(
            new CapturingHandler((_, _) => WavResponse(Wav(samples, sampleRate: 8_000))));

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None));

        Assert.Equal("Speech.AudioTooLong", ex.Message);
        Assert.Empty(playback.Requests);
    }

    [Theory]
    [InlineData("not audio")]
    [InlineData("8-bit")]
    [InlineData("empty")]
    public async Task SpeakAsync_RejectsUnplayableAudio(string kind)
    {
        var audio = kind switch
        {
            "not audio" => """{ "status": "ok" }"""u8.ToArray(),
            "8-bit" => Wav([1, 2], bitsPerSample: 8),
            _ => Wav([]),
        };
        var (sut, playback, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) => WavResponse(audio)));

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, ex.FailureKind);
        Assert.Empty(playback.Requests);
    }

    [Fact]
    public async Task SpeakAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var (sut, playback, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler runs inside the awaited call, before disposal.
            cts.Cancel();
            return WavResponse(Wav([1, 0]));
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello"), cts.Token));

        Assert.Empty(playback.Requests);
    }

    [Fact]
    public async Task SpeakAsync_TimesOutWhenTheAudioBodyStallsAfterTheHeaders()
    {
        var (sut, playback, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));
        sut.SpeechResponseDeadline = TimeSpan.FromMilliseconds(100);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Timeout, ex.FailureKind);
        Assert.Empty(playback.Requests);
    }

    [Fact]
    public async Task RefreshVoicesAsync_LoadsBothPoolsPairedWithTheirModelsAndPersistsThem()
    {
        var paths = new List<string>();
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer smallest-key", request.Headers.Authorization?.ToString());
            paths.Add(request.RequestUri!.AbsolutePath);
            return JsonResponse(request.RequestUri.AbsolutePath.Contains("-pro") ? ProVoices : StandardVoices);
        });
        var (sut, _, host) = await CreateSpeakingPluginAsync(handler);

        var result = await sut.RefreshVoicesAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["/waves/v1/lightning-v3.1/get_voices", "/waves/v1/lightning-v3.1-pro/get_voices"], paths);
        Assert.Equal(
            ["lightning_v3.1_pro:ben", "lightning_v3.1:devansh", "lightning_v3.1:magnus", "lightning_v3.1_pro:magnus"],
            sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal("Ben · Lightning Pro", sut.AvailableVoices[0].DisplayName);
        Assert.Equal("de", sut.AvailableVoices[0].LocaleIdentifier);
        Assert.Equal("hi", sut.AvailableVoices[1].LocaleIdentifier);
        Assert.Null(sut.AvailableVoices[3].LocaleIdentifier);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        // The cached catalog and selection survive reactivation without a network request.
        sut.SelectVoice("lightning_v3.1_pro:ben");
        using var offlineClient = new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException()));
        var restarted = new SmallestAiPlugin(offlineClient);
        await restarted.ActivateAsync(host);
        Assert.Equal(4, restarted.AvailableVoices.Count);
        Assert.Equal("lightning_v3.1_pro:ben", restarted.SelectedVoiceId);
    }

    [Fact]
    public async Task RefreshVoicesAsync_KeepsThePreviousCatalogWhenAPoolFails()
    {
        var failPro = false;
        var handler = new CapturingHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("-pro")
                // ReSharper disable once AccessToModifiedClosure -- the test flips failPro between refreshes on purpose.
                ? failPro ? JsonResponse("""{ "error": "down" }""", HttpStatusCode.InternalServerError) : JsonResponse(ProVoices)
                : JsonResponse(StandardVoices));
        var (sut, _, _) = await CreateSpeakingPluginAsync(handler);
        Assert.True((await sut.RefreshVoicesAsync(CancellationToken.None)).IsSuccess);
        sut.SelectVoice("lightning_v3.1_pro:ben");

        failPro = true;
        var result = await sut.RefreshVoicesAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(4, sut.AvailableVoices.Count);
        Assert.Equal("lightning_v3.1_pro:ben", sut.SelectedVoiceId);
    }

    [Theory]
    [InlineData("""{ "voices": "nope" }""")]
    [InlineData("not json")]
    [InlineData("""{ "voices": [] }""")]
    public async Task RefreshVoicesAsync_RejectsUnreadableOrEmptyCatalogs(string json)
    {
        var (sut, _, _) = await CreateSpeakingPluginAsync(new CapturingHandler((_, _) => JsonResponse(json)));

        var result = await sut.RefreshVoicesAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("lightning_v3.1:magnus", Assert.Single(sut.AvailableVoices).Id);
    }

    [Fact]
    public async Task RefreshVoicesAsync_DiscardsVoicesWhenTheKeyChangesMidRefresh()
    {
        SmallestAiPlugin? sut = null;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("-pro"))
                // ReSharper disable once AccessToModifiedClosure -- sut is assigned before the handler first runs.
                sut!.SetApiKeyAsync("other-key").GetAwaiter().GetResult();
            return JsonResponse(request.RequestUri.AbsolutePath.Contains("-pro") ? ProVoices : StandardVoices);
        });
        (sut, _, _) = await CreateSpeakingPluginAsync(handler);

        var result = await sut.RefreshVoicesAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Speech.ConnectionChanged", result.Message);
        Assert.Single(sut.AvailableVoices);
    }

    [Fact]
    public async Task SelectVoice_StoresOnlyCatalogVoicesAndFallsBackWhenARefreshDropsTheSelection()
    {
        var pro = ProVoices;
        var handler = new CapturingHandler((request, _) =>
            // ReSharper disable once AccessToModifiedClosure -- the test swaps the Pro catalog between refreshes on purpose.
            JsonResponse(request.RequestUri!.AbsolutePath.Contains("-pro") ? pro : StandardVoices));
        var (sut, _, _) = await CreateSpeakingPluginAsync(handler);
        await sut.RefreshVoicesAsync(CancellationToken.None);

        sut.SelectVoice("lightning_v3.1:unknown");
        Assert.Null(sut.SelectedVoiceId);

        sut.SelectVoice("lightning_v3.1_pro:ben");
        pro = """{ "voices": [] }""";
        await sut.RefreshVoicesAsync(CancellationToken.None);

        Assert.Null(sut.SelectedVoiceId);
        Assert.StartsWith("Magnus · Lightning;", sut.SettingsSummary);
    }

    [Fact]
    public async Task SpeakAsync_DefaultStaysMagnusAfterARefreshWhereAnotherVoiceSortsFirst()
    {
        var models = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(request.RequestUri!.AbsolutePath.Contains("-pro") ? ProVoices : StandardVoices);

            var sent = JsonDocument.Parse(body!).RootElement;
            models.Add($"{sent.GetProperty("model").GetString()}:{sent.GetProperty("voice_id").GetString()}");
            return WavResponse(Wav([1, 0]));
        });
        var (sut, _, _) = await CreateSpeakingPluginAsync(handler);
        await sut.RefreshVoicesAsync(CancellationToken.None);
        Assert.Equal("lightning_v3.1_pro:ben", sut.AvailableVoices[0].Id);

        await sut.SpeakAsync(new TtsSpeakRequest("Hello"), CancellationToken.None);

        Assert.Equal(["lightning_v3.1:magnus"], models);
    }

    [Fact]
    public async Task SetApiKeyAsync_DropsThePreviousAccountsVoicesAndSelection()
    {
        var handler = new CapturingHandler((request, _) =>
            JsonResponse(request.RequestUri!.AbsolutePath.Contains("-pro") ? ProVoices : StandardVoices));
        var (sut, _, host) = await CreateSpeakingPluginAsync(handler);
        await sut.RefreshVoicesAsync(CancellationToken.None);
        sut.SelectVoice("lightning_v3.1_pro:ben");
        var notifications = host.NotifyCapabilitiesChangedCount;

        await sut.SetApiKeyAsync("other-key");

        Assert.Equal("lightning_v3.1:magnus", Assert.Single(sut.AvailableVoices).Id);
        Assert.Null(sut.SelectedVoiceId);
        Assert.Null(host.GetSetting<List<SmallestAiVoice>>("speechVoices"));
        Assert.Null(host.GetSetting<string>("speechVoice"));
        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);

        // Saving the same key again keeps the refreshed catalog.
        await sut.RefreshVoicesAsync(CancellationToken.None);
        await sut.SetApiKeyAsync(" other-key ");
        Assert.Equal(4, sut.AvailableVoices.Count);
    }

    [Fact]
    public async Task ValidateAsync_SkipsTheVoiceRefreshWhileThePluginIsNotActivated()
    {
        var paths = new List<string>();
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return JsonResponse("""{ "error": "probe" }""", HttpStatusCode.BadRequest);
        }));
        var sut = new SmallestAiPlugin(httpClient);
        await sut.SetApiKeyAsync("smallest-key");

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(["/waves/v1/pulse/get_text"], paths);
    }

    [Fact]
    public async Task SettingsActions_RefreshVoicesOnlyWhenConfigured()
    {
        var sut = new SmallestAiPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        var action = Assert.Single(sut.GetSettingsActions());
        Assert.Equal("refresh-voices", action.Id);
        Assert.False(action.IsEnabled);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.ExecuteSettingsActionAsync("other", CancellationToken.None));

        var result = await sut.ExecuteSettingsActionAsync("refresh-voices", CancellationToken.None);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void WavParse_SkipsPaddedChunksAndKeepsWholeFramesOfAnOversizedDataChunk()
    {
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(new byte[4]);
        wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange(Chunk("fmt ", FmtBody(16_000, 1, 16)));
        // ReSharper disable once UseUtf8StringLiteral -- arbitrary filler bytes, not text.
        wav.AddRange(Chunk("LIST", [9, 9, 9])); // odd size, padded
        wav.AddRange("data"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(uint.MaxValue)); // streamed: unknown length
        wav.AddRange([1, 0, 2, 0, 3]); // trailing half sample

        var audio = SmallestAiWav.Parse(wav.ToArray());

        Assert.NotNull(audio);
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, audio.Samples.ToArray());
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
    }

    [Fact]
    public void WavParse_RejectsDataBeforeFormatAndTruncatedChunks()
    {
        var dataFirst = Concat("RIFF"u8.ToArray(), new byte[4], "WAVE"u8.ToArray(), Chunk("data", [1, 0]), Chunk("fmt ", FmtBody(16_000, 1, 16)));
        var truncated = Concat("RIFF"u8.ToArray(), new byte[4], "WAVE"u8.ToArray(), "fmt "u8.ToArray(), BitConverter.GetBytes(16u), new byte[4]);

        Assert.Null(SmallestAiWav.Parse(dataFirst));
        Assert.Null(SmallestAiWav.Parse(truncated));
    }

    private static async Task<(SmallestAiPlugin Plugin, RecordingPcmPlayback Playback, TestPluginHostServices Host)> CreateSpeakingPluginAsync(
        HttpMessageHandler handler)
    {
        var playback = new RecordingPcmPlayback();
        var host = new TestPluginHostServices
        {
            Secrets = { ["api-key"] = "smallest-key" },
            PcmPlayback = playback,
        };
        var sut = new SmallestAiPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return (sut, playback, host);
    }

    private static HttpResponseMessage WavResponse(byte[] audio)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return response;
    }

    private static byte[] Wav(byte[] samples, int sampleRate = 24_000, int channels = 1, int bitsPerSample = 16) =>
        Concat(
            "RIFF"u8.ToArray(),
            BitConverter.GetBytes(36 + samples.Length),
            "WAVE"u8.ToArray(),
            Chunk("fmt ", FmtBody(sampleRate, channels, bitsPerSample)),
            Chunk("data", samples));

    private static byte[] FmtBody(int sampleRate, int channels, int bitsPerSample)
    {
        var body = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), (ushort)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), (ushort)bitsPerSample);
        return body;
    }

    private static byte[] Chunk(string id, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(id), BitConverter.GetBytes(body.Length), body, body.Length % 2 == 1 ? [0] : []);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class RecordingPcmPlayback : IPluginPcmPlaybackService
    {
        public List<PcmPlaybackRequest> Requests { get; } = [];
        public ITtsPlaybackSession Session { get; } = new ActiveSession();
        public bool IsAvailable => true;

        public Task<ITtsPlaybackSession> PlayAsync(PcmPlaybackRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with { Payload = request.Payload.ToArray() });
            return Task.FromResult(Session);
        }

        private sealed class ActiveSession : ITtsPlaybackSession
        {
            public bool IsActive => true;

            public event EventHandler? Completed
            {
                add { }
                remove { }
            }

            public void Stop()
            {
            }
        }
    }

    // Headers arrive, then the body never does.
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A body with no Content-Length, so only the streaming read can catch its size.
    private sealed class UnknownLengthStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
