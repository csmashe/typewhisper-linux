extern alias OpenAiCompatible;
using OpenAiRealtimeStreamingSession = OpenAiCompatible::TypeWhisper.Plugins.Shared.OpenAi.OpenAiRealtimeStreamingSession;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiCompatiblePluginTests
{
    [Theory]
    [InlineData("auto", "gpt-live-transcribe-2026-01-01", true)]
    [InlineData("auto", "gpt-live-transcribe", true)]
    [InlineData("auto", "gpt-realtime-whisper", true)]
    [InlineData("auto", "gpt-realtime-whisper-2026-01-01", true)]
    [InlineData("auto", "whisper-1", false)]
    [InlineData("auto", "gpt-live-transcriber", false)]
    [InlineData("auto", "gpt-realtime-whispering", false)]
    [InlineData("auto", null, false)]
    [InlineData("realtime", "my-alias", true)]
    [InlineData("batch", "gpt-live-transcribe", false)]
    public void UsesRealtime_SelectsTransport(string mode, string? model, bool expected) =>
        Assert.Equal(expected, OpenAiCompatiblePlugin.UsesRealtime(mode, model));

    [Theory]
    [InlineData("auto", "gpt-realtime-whisper", false)]
    [InlineData("auto", "gpt-realtime-whisper-2026-01-01", false)]
    [InlineData("auto", "gpt-realtime-whispering", true)]
    [InlineData("auto", "gpt-live-transcribe", true)]
    [InlineData("auto", "my-alias", true)]
    [InlineData("live", "gpt-realtime-whisper", true)]
    [InlineData("live", "my-alias", true)]
    [InlineData("whisper", "gpt-live-transcribe", false)]
    [InlineData("whisper", "my-alias", false)]
    public void IsLiveModel_RespectsProtocol(string protocol, string model, bool expected) =>
        Assert.Equal(expected, OpenAiRealtimeStreamingSession.IsLiveModel(model, protocol));

    [Theory]
    [InlineData("https://x", "", "wss://x/v1/realtime?intent=transcription")]
    [InlineData("https://x", "2025-03-01-preview", "wss://x/v1/realtime?api-version=2025-03-01-preview&intent=transcription")]
    [InlineData("http://x", "", "ws://x/v1/realtime?intent=transcription")]
    [InlineData("http://x:8080/base/", "", "ws://x:8080/base/v1/realtime?intent=transcription")]
    public void RealtimeUri_PreservesVersionPathAndPort(string baseUrl, string version, string expected) =>
        Assert.Equal(expected, OpenAiCompatiblePlugin.RealtimeUri(baseUrl, version).AbsoluteUri);

    [Theory]
    [InlineData(false, false, "auto", "gpt-live-transcribe", "auto", 0, true)]
    [InlineData(false, true, "realtime", "my-alias", "live", 2, true)]
    [InlineData(false, false, "realtime", "my-alias", "whisper", 1, false)]
    [InlineData(true, false, "auto", "gpt-realtime-whisper-2026-01-01", "auto", 0, false)]
    [InlineData(true, true, "realtime", "my-alias", "live", 2, true)]
    [InlineData(true, false, "realtime", "my-alias", "whisper", 1, false)]
    public async Task RealtimeRole_ConnectsWithEndpointHeadersAndSessionUpdate(
        bool additional, bool azure, string mode, string model, string protocol, int overload, bool live)
    {
        var host = new TestPluginHostServices();
        var baseUrl = azure ? "https://test.openai.azure.com" : "https://example.test";
        const string version = "2025-03-01-preview";
        if (additional)
        {
            host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
            {
                new() { Id = "openai-compatible-realtime", Name = "Realtime", BaseUrl = baseUrl,
                    ApiVersion = version, SelectedModelId = model, TranscriptionMode = mode, RealtimeProtocol = protocol },
            });
            host.Secrets["api-key.openai-compatible-realtime"] = "key";
        }
        else
        {
            host.SetSetting("baseUrl", baseUrl);
            host.SetSetting("apiVersion", version);
            host.SetSetting("selectedModel", model);
            host.SetSetting("transcriptionMode", mode);
            host.SetSetting("realtimeProtocol", protocol);
            host.Secrets["api-key"] = "key";
        }
        using var client = new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request")));
        var transport = new ScriptedWebSocketTransport(connected: false);
        using var sut = new OpenAiCompatiblePlugin(client, transportFactory: new ScriptedWebSocketTransportFactory(transport));
        await sut.ActivateAsync(host);
        var role = additional ? Assert.Single(sut.AdditionalTranscriptionEngines) : sut;
        Assert.True(role.SupportsStreaming);
        Assert.False(role.SupportsTranslation);
        Assert.Equal(live, role.SupportsLanguageHints);
        await using var session = overload switch
        {
            0 => await role.StartStreamingAsync("de", CancellationToken.None),
            1 => await role.StartStreamingWithLanguageHintsAsync(["de", "en"], CancellationToken.None),
            _ => await role.StartStreamingWithLanguageHintsAndPromptAsync(["de", "en"], "dictionary terms", CancellationToken.None),
        };
        Assert.Null(Assert.IsType<IStreamingSessionHealth>(session, exactMatch: false).Fault);
        var options = Assert.IsType<WebSocketConnectionOptions>(transport.ConnectionOptions);
        Assert.Equal(baseUrl.Replace("https://", "wss://") + "/v1/realtime?api-version=" + version + "&intent=transcription", options.Uri.AbsoluteUri);
        Assert.Equal("Bearer key", options.Headers!["Authorization"]);
        Assert.Equal(azure, options.Headers.ContainsKey("api-key"));
        if (azure)
            Assert.Equal("key", options.Headers["api-key"]);
        using var payload = JsonDocument.Parse((await transport.NextSentAsync()).Payload);
        Assert.Equal("session.update", payload.RootElement.GetProperty("type").GetString());
        var input = payload.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        // Live streaming needs server VAD so utterances commit before the user stops dictating.
        Assert.Equal("server_vad", input.GetProperty("turn_detection").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        var transcription = input.GetProperty("transcription");
        Assert.Equal(model, transcription.GetProperty("model").GetString());
        if (live)
        {
            string[] expectedLanguages = overload == 0 ? ["de"] : ["de", "en"];
            Assert.Equal(expectedLanguages,
                transcription.GetProperty("languages").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("low", transcription.GetProperty("delay").GetString());
            Assert.False(transcription.TryGetProperty("language", out _));
        }
        else
        {
            Assert.Equal("de", transcription.GetProperty("language").GetString());
            Assert.False(transcription.TryGetProperty("languages", out _));
            Assert.False(transcription.TryGetProperty("delay", out _));
        }
        if (overload == 2)
            Assert.Equal("dictionary terms", transcription.GetProperty("prompt").GetString());
        else
            Assert.False(transcription.TryGetProperty("prompt", out _));
    }

    [Theory]
    [InlineData(false, "realtime")]
    [InlineData(true, "realtime")]
    [InlineData(false, "batch")]
    [InlineData(true, "batch")]
    public async Task Translation_RejectsRealtimeBeforeHttpAndPreservesBatch(bool additional, string mode)
    {
        var calls = 0;
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            calls++;
            Assert.EndsWith("/v1/audio/translations", request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"translated"}""") };
        }));
        var host = new TestPluginHostServices { Localization = TimeoutLocalization() };
        host.SetSetting("baseUrl", "https://example.test");
        host.SetSetting("selectedModel", "gpt-live-transcribe");
        host.SetSetting("transcriptionMode", mode);
        host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
        {
            new() { Id = "openai-compatible-realtime", BaseUrl = "https://example.test",
                SelectedModelId = "gpt-live-transcribe", TranscriptionMode = mode },
        });
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var role = additional ? Assert.Single(sut.AdditionalTranscriptionEngines) : sut;
        Assert.Equal(mode == "batch", role.SupportsTranslation);
        if (mode == "realtime")
        {
            var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.TranscribeAsync([], "de", true, null, CancellationToken.None));
            Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
            Assert.Equal(host.Localization.GetString("Settings.RealtimeNoTranslation"), error.Message);
            Assert.Equal(0, calls);
        }
        else
        {
            Assert.False(role.SupportsStreaming);
            Assert.False(role.SupportsLanguageHints);
            await role.TranscribeAsync([], "de", true, null, CancellationToken.None);
            Assert.Equal(1, calls);
            await Assert.ThrowsAsync<NotSupportedException>(() => role.StartStreamingAsync("de", CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(false, "auto", "gpt-realtime-whisper", "auto", "de")]
    [InlineData(false, "realtime", "my-alias", "live", null)]
    [InlineData(true, "realtime", "my-alias", "whisper", "de")]
    [InlineData(true, "auto", "gpt-live-transcribe", "auto", null)]
    public async Task RealtimeRole_TranscribesRecordedAudioOverWebSocket(
        bool additional, string mode, string model, string protocol, string? expectedLanguage)
    {
        var host = new TestPluginHostServices();
        if (additional)
        {
            host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
            {
                new() { Id = "openai-compatible-realtime", Name = "Realtime", BaseUrl = "https://example.test",
                    SelectedModelId = model, TranscriptionMode = mode, RealtimeProtocol = protocol },
            });
            host.Secrets["api-key.openai-compatible-realtime"] = "key";
        }
        else
        {
            host.SetSetting("baseUrl", "https://example.test");
            host.SetSetting("selectedModel", model);
            host.SetSetting("transcriptionMode", mode);
            host.SetSetting("realtimeProtocol", protocol);
            host.Secrets["api-key"] = "key";
        }
        using var client = new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request")));
        var transport = new ScriptedWebSocketTransport(connected: false);
        using var sut = new OpenAiCompatiblePlugin(client, transportFactory: new ScriptedWebSocketTransportFactory(transport));
        await sut.ActivateAsync(host);
        var role = additional ? Assert.Single(sut.AdditionalTranscriptionEngines) : sut;

        var transcribe = role.TranscribeWithLanguageHintsAsync(
            BuildPcm16Wav([1, 0, 2, 0]), ["de", "auto", "en", "de"], false, "dictionary terms", CancellationToken.None);
        using var update = JsonDocument.Parse((await transport.NextSentAsync()).Payload);
        Assert.Equal("session.update", update.RootElement.GetProperty("type").GetString());
        var input = update.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        // Whole-recording upload commits once at the end, so no server VAD.
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        var transcription = input.GetProperty("transcription");
        Assert.Equal(model, transcription.GetProperty("model").GetString());
        Assert.Equal("dictionary terms", transcription.GetProperty("prompt").GetString());
        if (expectedLanguage is null)
            Assert.Equal(["de", "en"], transcription.GetProperty("languages").EnumerateArray().Select(e => e.GetString()));
        else
            Assert.Equal("de", transcription.GetProperty("language").GetString());
        Assert.Equal("wss://example.test/v1/realtime?intent=transcription", transport.ConnectionOptions!.Uri.AbsoluteUri);
        Assert.Equal("Bearer key", transport.ConnectionOptions.Headers!["Authorization"]);
        string? type;
        do
        {
            using var sent = JsonDocument.Parse((await transport.NextSentAsync()).Payload);
            type = sent.RootElement.GetProperty("type").GetString();
        } while (type != "input_audio_buffer.commit");
        transport.EnqueueText("""{"type":"input_audio_buffer.committed","item_id":"item_1"}""");
        transport.EnqueueText("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"hallo welt"}""");

        var result = await transcribe.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("hallo welt", result.Text);
        Assert.Equal(expectedLanguage, result.DetectedLanguage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealtimeRole_ProgressOverloadForwardsAllLanguageHints(bool additional)
    {
        var host = new TestPluginHostServices();
        if (additional)
        {
            host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
            {
                new() { Id = "openai-compatible-realtime", Name = "Realtime", BaseUrl = "https://example.test",
                    SelectedModelId = "gpt-live-transcribe" },
            });
            host.Secrets["api-key.openai-compatible-realtime"] = "key";
        }
        else
        {
            host.SetSetting("baseUrl", "https://example.test");
            host.SetSetting("selectedModel", "gpt-live-transcribe");
            host.Secrets["api-key"] = "key";
        }
        using var client = new HttpClient(new CapturingHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP request")));
        var transport = new ScriptedWebSocketTransport(connected: false);
        using var sut = new OpenAiCompatiblePlugin(client, transportFactory: new ScriptedWebSocketTransportFactory(transport));
        await sut.ActivateAsync(host);
        var role = additional ? Assert.Single(sut.AdditionalTranscriptionEngines) : sut;

        var transcribe = role.TranscribeStreamingWithLanguageHintsAsync(
            BuildPcm16Wav([1, 0, 2, 0]), ["de", "en"], false, null, _ => true, CancellationToken.None);
        using var update = JsonDocument.Parse((await transport.NextSentAsync()).Payload);
        Assert.Equal("session.update", update.RootElement.GetProperty("type").GetString());
        var transcription = update.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Assert.Equal(["de", "en"], transcription.GetProperty("languages").EnumerateArray().Select(e => e.GetString()));
        string? type;
        do
        {
            using var sent = JsonDocument.Parse((await transport.NextSentAsync()).Payload);
            type = sent.RootElement.GetProperty("type").GetString();
        } while (type != "input_audio_buffer.commit");
        transport.EnqueueText("""{"type":"input_audio_buffer.committed","item_id":"item_1"}""");
        transport.EnqueueText("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"hallo welt"}""");

        Assert.Equal("hallo welt", (await transcribe.WaitAsync(TimeSpan.FromSeconds(10))).Text);
    }

    private static byte[] BuildPcm16Wav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealtimeWithoutModel_IsConfigurationFailure(bool additional)
    {
        var host = new TestPluginHostServices { Localization = TimeoutLocalization() };
        host.SetSetting("baseUrl", "https://example.test");
        host.SetSetting("transcriptionMode", "realtime");
        host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
        {
            new() { Id = "openai-compatible-realtime", BaseUrl = "https://example.test", TranscriptionMode = "realtime" },
        });
        using var sut = new OpenAiCompatiblePlugin();
        await sut.ActivateAsync(host);
        var role = additional ? Assert.Single(sut.AdditionalTranscriptionEngines) : sut;
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => role.StartStreamingAsync(null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.Equal(host.Localization.GetString("Settings.NoTranscriptionModelSelected"), error.Message);
    }

    [Theory]
    [InlineData("transcriptionMode", "realtime")]
    [InlineData("realtimeProtocol", "whisper")]
    public async Task RealtimeOptions_RoundTripAndInvalidateProfileRole(string key, string value)
    {
        using var client = ModelsClient();
        var host = CachedProfileHost();
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        Assert.Equal("auto", await sut.GetSettingValueAsync(key));
        await sut.SetSettingValueAsync(key, "unknown");
        Assert.Equal("auto", await sut.GetSettingValueAsync(key));
        await sut.SetSettingValueAsync(key, value);
        Assert.Equal(value, host.GetSetting<string>(key));
        var oldRole = Assert.Single(sut.AdditionalTranscriptionEngines);
        var values = Assert.Single(await sut.GetItemsAsync("profiles")).Values.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("auto", values[key]);
        values[key] = value;
        Assert.True((await sut.SetItemsAsync("profiles", [new PluginCollectionItem(values)])).IsSuccess);
        Assert.NotSame(oldRole, Assert.Single(sut.AdditionalTranscriptionEngines));
        Assert.Equal(oldRole.SelectedModelId, Assert.Single(sut.AdditionalTranscriptionEngines).SelectedModelId);
        using var reloaded = new OpenAiCompatiblePlugin(client);
        await reloaded.ActivateAsync(host);
        Assert.Equal(value, await reloaded.GetSettingValueAsync(key));
        Assert.Equal(value, Assert.Single(await reloaded.GetItemsAsync("profiles")).Values[key]);
        var flat = Assert.Single(sut.GetSettingDefinitions(), d => d.Key == key);
        var collection = Assert.Single(Assert.Single(sut.GetCollectionDefinitions()).ItemFields, d => d.Key == key);
        Assert.Equal(PluginSettingKind.Dropdown, flat.Kind);
        Assert.Equal(flat.Label, collection.Label);
        Assert.Equal(flat.Description, collection.Description);
        Assert.Equal(flat.Options, collection.Options);
    }

    [Fact]
    public async Task UnknownRealtimeProfileOptions_NormalizeToAuto()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
        {
            new() { Id = "openai-compatible-realtime", BaseUrl = "https://example.test",
                TranscriptionMode = "unknown", RealtimeProtocol = "unknown" },
        });
        using var sut = new OpenAiCompatiblePlugin();
        await sut.ActivateAsync(host);
        var saved = Assert.Single(host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!);
        Assert.Equal("auto", saved.TranscriptionMode);
        Assert.Equal("auto", saved.RealtimeProtocol);
    }

    [Fact]
    public void RequestUri_AppendsVersionAndRejectsNonHttpBaseUrl()
    {
        Assert.Equal("https://x/v1/models?api-version=2025-03-01-preview",
            OpenAiCompatiblePlugin.RequestUri("https://x/", "2025-03-01-preview", "/v1/models").AbsoluteUri);
        Assert.Equal("https://x/v1/models?api-version=a%20b%26c",
            OpenAiCompatiblePlugin.RequestUri("https://x", " a b&c ", "v1/models").AbsoluteUri);
        Assert.Equal("https://x/v1/models",
            OpenAiCompatiblePlugin.RequestUri("https://x", "", "v1/models").AbsoluteUri);
        foreach (var url in new[] { "ftp://x", "relative" })
            Assert.Equal(PluginRequestFailureKind.Configuration,
                Assert.Throws<PluginRequestException>(() => OpenAiCompatiblePlugin.RequestUri(url, "", "v1/models")).FailureKind);
    }

    [Theory]
    [InlineData("standard", "", false, "/v1/audio/transcriptions")]
    [InlineData("standard", "", true, "/v1/audio/translations")]
    [InlineData("deployment-scoped", "2025-03-01-preview", false, "/deployments/my%2Fmodel%20id/audio/transcriptions?api-version=2025-03-01-preview")]
    [InlineData("deployment-scoped", "2025-03-01-preview", true, "/deployments/my%2Fmodel%20id/audio/translations?api-version=2025-03-01-preview")]
    public void BatchUri_SelectsRoute(string batch, string version, bool translate, string expected)
    {
        Assert.Equal("https://x" + expected,
            OpenAiCompatiblePlugin.BatchUri("https://x", version, batch, "my/model id", translate).AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("preview")]
    [InlineData("v2025-03-01")]
    [InlineData("2025-3-1")]
    public void BatchUri_RequiresDatedVersion(string version)
    {
        Assert.False(OpenAiCompatiblePlugin.IsDatedApiVersion(version));
        var error = Assert.Throws<PluginRequestException>(() =>
            OpenAiCompatiblePlugin.BatchUri("https://x", version, "deployment-scoped", "whisper", false));
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.Equal("Deployment-scoped transcription requires a dated API version, such as 2025-03-01-preview.", error.Message);
    }

    [Theory]
    [InlineData("2025-03-01")]
    [InlineData("2025-03-01-preview")]
    public void IsDatedApiVersion_AcceptsDatePrefix(string version) =>
        Assert.True(OpenAiCompatiblePlugin.IsDatedApiVersion(version));

    [Theory]
    [InlineData("https://api.openai.com", false)]
    [InlineData("https://foo.openai.azure.com", true)]
    [InlineData("https://FOO.OPENAI.AZURE.US", true)]
    [InlineData("https://foo.services.ai.azure.com", true)]
    [InlineData("https://foo.openai.azure.com.example.test", false)]
    public void AuthenticationHeaders_UsesAzureSuffixes(string url, bool azure)
    {
        var headers = OpenAiCompatiblePlugin.AuthenticationHeaders(new Uri(url), "key");
        Assert.Equal("Bearer key", headers["Authorization"]);
        Assert.Equal(azure ? 2 : 1, headers.Count);
        if (azure)
            Assert.Equal("key", headers["api-key"]);
        foreach (var blank in new[] { null, "", "  " })
            Assert.Empty(OpenAiCompatiblePlugin.AuthenticationHeaders(new Uri(url), blank));
    }

    [Theory]
    [InlineData(false, "deployment-scoped", "2025-03-01-preview", false)]
    [InlineData(true, "deployment-scoped", "2025-03-01-preview", false)]
    [InlineData(false, "deployment-scoped", "2025-03-01-preview", true)]
    [InlineData(true, "deployment-scoped", "2025-03-01-preview", true)]
    [InlineData(false, "standard", "2025-03-01-preview", false)]
    [InlineData(true, "standard", "2025-03-01-preview", false)]
    [InlineData(false, "standard", "", false)]
    [InlineData(true, "standard", "", false)]
    public async Task BatchRequests_UseEndpointOptions(bool profile, string batch, string version, bool translate)
    {
        var postCount = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
                return ModelCatalogResponse("whisper");
            postCount++;
            Assert.Equal(OpenAiCompatiblePlugin.BatchUri("https://foo.openai.azure.com", version, batch, "whisper", translate), request.RequestUri);
            Assert.Equal("Bearer key", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.Equal("key", Assert.Single(request.Headers.GetValues("api-key")));
            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var format = content.Single(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "response_format");
            Assert.Equal(batch == "deployment-scoped" || version.Length > 0 ? "json" : "verbose_json",
                await format.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"ok"}""") };
        }));
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://foo.openai.azure.com");
        host.SetSetting("apiVersion", version);
        host.SetSetting("batchEndpoint", batch);
        host.SetSetting("selectedModel", "whisper");
        host.Secrets["api-key"] = "key";
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        ITranscriptionEngineRole role = sut;
        if (profile)
        {
            Assert.True((await sut.SetItemsAsync("profiles", [ProfileItem("Azure", "https://foo.openai.azure.com",
                apiKey: "key", model: "whisper", apiVersion: version, batchEndpoint: batch)])).IsSuccess);
            role = Assert.Single(sut.AdditionalTranscriptionEngines);
        }
        var result = await role.TranscribeAsync([], null, translate, null, CancellationToken.None);
        Assert.Equal("ok", result.Text);
        Assert.Empty(result.Segments);
        Assert.Equal(1, postCount);
    }

    [Theory]
    [InlineData(false, "chat-completions", false)]
    [InlineData(true, "chat-completions", false)]
    [InlineData(false, "chat-completions", true)]
    [InlineData(true, "chat-completions", true)]
    [InlineData(false, "responses", false)]
    [InlineData(true, "responses", false)]
    [InlineData(false, "responses", true)]
    [InlineData(true, "responses", true)]
    public async Task VersionedRequests_UseAzureHeadersForTextAndModels(bool profile, string api, bool streaming)
    {
        var paths = new List<string>();
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            Assert.Equal("?api-version=2025-03-01-preview", request.RequestUri!.Query);
            Assert.Equal("Bearer key", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.Equal("key", Assert.Single(request.Headers.GetValues("api-key")));
            paths.Add(request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return ModelCatalogResponse("m1");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(api == "responses" ? """{"output_text":"ok"}"""
                    : streaming ? "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                    : """{"choices":[{"message":{"content":"ok"}}]}"""),
            };
        }));
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://foo.openai.azure.com");
        host.SetSetting("apiVersion", " 2025-03-01-preview ");
        host.SetSetting("textApi", api);
        host.Secrets["api-key"] = "key";
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        ILlmProviderRole role = sut;
        if (profile)
        {
            Assert.True((await sut.SetItemsAsync("profiles", [ProfileItem("Azure", "https://foo.openai.azure.com",
                apiKey: "key", llmModel: "m1", apiVersion: " 2025-03-01-preview ", textApi: api)])).IsSuccess);
            role = Assert.Single(sut.AdditionalLlmProviders);
        }
        else
        {
            Assert.True(await sut.ValidateConnectionAsync());
            Assert.NotNull(await sut.FetchModelsAsync());
            Assert.True((await sut.ValidateAsync())!.IsSuccess);
        }
        Assert.Equal(["ok"], await ProcessTextOptionsAsync(role, streaming));
        Assert.Contains("/v1/models", paths);
        Assert.Equal(api == "responses" ? "/v1/responses" : "/v1/chat/completions", paths[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("preview")]
    public async Task DeploymentSetting_RejectsMissingDateWithLocalizedMessage(string version)
    {
        using var client = ModelsClient();
        var host = new TestPluginHostServices { Localization = TimeoutLocalization() };
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("apiVersion", version);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("batchEndpoint", "deployment-scoped"));
        Assert.Equal(host.Localization.GetString("Settings.ApiVersionRequired"), error.Message);
        Assert.Equal("standard", await sut.GetSettingValueAsync("batchEndpoint"));
        var result = await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://x", batchEndpoint: "deployment-scoped", apiVersion: version)]);
        Assert.False(result.IsSuccess);
        Assert.Equal(error.Message, result.Message);
        Assert.Empty(await sut.GetItemsAsync("profiles"));
    }

    [Fact]
    public async Task DeploymentSetting_SwitchingToStandardWhileClearingVersionSaves()
    {
        using var client = ModelsClient();
        var host = new TestPluginHostServices { Localization = TimeoutLocalization() };
        host.SetSetting("apiVersion", "2025-03-01-preview");
        host.SetSetting("batchEndpoint", "deployment-scoped");
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        // Form order: the cleared version arrives while the old route is still deployment-scoped.
        await sut.SetSettingValueAsync("apiVersion", "");
        await sut.SetSettingValueAsync("batchEndpoint", "standard");
        Assert.Equal("", await sut.GetSettingValueAsync("apiVersion"));
        Assert.Equal("standard", await sut.GetSettingValueAsync("batchEndpoint"));
        Assert.Equal("standard", host.GetSetting<string>("batchEndpoint"));
    }

    [Theory]
    [InlineData("apiVersion", "2025-04-01-preview")]
    [InlineData("batchEndpoint", "deployment-scoped")]
    public async Task DefaultEndpoint_ChangingRouteClearsModels(string key, string value)
    {
        using var client = ModelsClient();
        var host = CachedDefaultHost();
        host.SetSetting("apiVersion", "2025-03-01-preview");
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("apiVersion", " 2025-03-01-preview ");
        await sut.SetSettingValueAsync("batchEndpoint", "standard");
        Assert.Single(sut.FetchedModels);
        await sut.SetSettingValueAsync(key, value);
        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Equal(value, host.GetSetting<string>(key));
        Assert.Equal(value, await sut.GetSettingValueAsync(key));
    }

    [Theory]
    [InlineData("apiVersion", "2025-04-01-preview", true)]
    [InlineData("batchEndpoint", "deployment-scoped", true)]
    [InlineData("apiVersion", "2025-04-01-preview", false)]
    [InlineData("batchEndpoint", "deployment-scoped", false)]
    public async Task ProfileRouteOptions_RoundTripAndInvalidateRoleAndCatalog(string key, string value, bool hasCatalog)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var host = CachedProfileHost();
        var profiles = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        profiles[0].ApiVersion = "2025-03-01-preview";
        if (!hasCatalog)
        {
            profiles[0].FetchedModels = [];
            profiles[0].SelectedModelId = null;
            profiles[0].SelectedLlmModelId = null;
        }
        host.SetSetting("additionalProfiles", profiles);
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var oldRole = Assert.Single(sut.AdditionalLlmProviders);
        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var values = item.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values[key] = value;
        Assert.True((await sut.SetItemsAsync("profiles", [new PluginCollectionItem(values)])).IsSuccess);
        Assert.NotSame(oldRole, Assert.Single(sut.AdditionalLlmProviders));
        var saved = Assert.Single(host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!);
        Assert.Empty(saved.FetchedModels);
        Assert.Null(saved.SelectedModelId);
        Assert.Null(saved.SelectedLlmModelId);
        using var reloaded = new OpenAiCompatiblePlugin(client);
        await reloaded.ActivateAsync(host);
        var roundTrip = Assert.Single(await reloaded.GetItemsAsync("profiles"));
        Assert.Equal(values["apiVersion"], roundTrip.Values["apiVersion"]);
        Assert.Equal(values["batchEndpoint"], roundTrip.Values["batchEndpoint"]);
    }

    [Theory]
    [InlineData("responses", "high", "custom", 0.7, null, false)]
    [InlineData("responses", "", "custom", 0.7, 0.7, false)]
    [InlineData("responses", "", "provider-default", 0.7, null, false)]
    [InlineData("responses", "high", "custom", 0.7, null, true)]
    [InlineData("responses", "", "custom", 0.7, 0.7, true)]
    [InlineData("chat-completions", "", "provider-default", 0.3, null, false)]
    [InlineData("chat-completions", "high", "custom", 1.5, 1.5, false)]
    [InlineData("chat-completions", "", "provider-default", 0.3, null, true)]
    [InlineData("chat-completions", "", "custom", 1.5, 1.5, true)]
    public async Task DefaultEndpoint_TextOptionsShapeRequests(
        string api, string effort, string temperatureMode, double temperature, double? expectedTemperature, bool streaming)
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var client = TextOptionsClient(requests);
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://example.test");
        host.Secrets["api-key"] = "key";
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("textApi", api);
        await sut.SetSettingValueAsync("reasoningEffort", effort);
        await sut.SetSettingValueAsync("temperatureMode", temperatureMode);
        await sut.SetSettingValueAsync("temperature", temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var chunks = await ProcessTextOptionsAsync(sut, streaming);
        Assert.Equal([api == "responses" ? "response" : "chat"], chunks);
        var request = Assert.Single(requests);
        Assert.Equal(api == "responses" ? "/v1/responses" : "/v1/chat/completions", request.Path);
        Assert.Equal(expectedTemperature.HasValue, request.Body.TryGetProperty("temperature", out var value));
        if (expectedTemperature.HasValue)
            Assert.Equal(expectedTemperature.Value, value.GetDouble());
        if (api == "responses")
        {
            Assert.Equal("message", request.Body.GetProperty("input")[0].GetProperty("type").GetString());
            Assert.Equal(effort.Length > 0, request.Body.TryGetProperty("reasoning", out var reasoning));
            if (effort.Length > 0)
                Assert.Equal(effort, reasoning.GetProperty("effort").GetString());
        }
        else
            Assert.False(request.Body.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditionalProfile_TextOptionsRoundTripAndUseResponses(bool streaming)
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var client = TextOptionsClient(requests);
        var host = new TestPluginHostServices();
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var result = await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", apiKey: "key",
            llmModel: "m1", textApi: "responses", reasoningEffort: "high", temperatureMode: "custom", temperature: "0.7")]);
        Assert.True(result.IsSuccess);
        var reloaded = new OpenAiCompatiblePlugin(client);
        await reloaded.ActivateAsync(host);
        var item = Assert.Single(await reloaded.GetItemsAsync("profiles"));
        Assert.Equal("responses", item.Values["textApi"]);
        Assert.Equal("high", item.Values["reasoningEffort"]);
        Assert.Equal("custom", item.Values["temperatureMode"]);
        Assert.Equal("0.7", item.Values["temperature"]);
        Assert.Equal(["response"], await ProcessTextOptionsAsync(Assert.Single(reloaded.AdditionalLlmProviders), streaming));
        var request = Assert.Single(requests);
        Assert.Equal("/v1/responses", request.Path);
        Assert.Equal("high", request.Body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(request.Body.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task AdditionalProfile_ChangingOnlyTextApiInvalidatesRoleAndChangesRequest()
    {
        var requests = new List<(string Path, JsonElement Body)>();
        using var client = TextOptionsClient(requests);
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", apiKey: "key", llmModel: "m1")]);
        var originalRole = Assert.Single(sut.AdditionalLlmProviders);
        Assert.Equal(["chat"], await ProcessTextOptionsAsync(originalRole, false));
        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var values = item.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["textApi"] = "responses";
        Assert.True((await sut.SetItemsAsync("profiles", [new PluginCollectionItem(values)])).IsSuccess);
        var newRole = Assert.Single(sut.AdditionalLlmProviders);
        Assert.NotSame(originalRole, newRole);
        Assert.Equal(["response"], await ProcessTextOptionsAsync(newRole, false));
        Assert.Equal(["/v1/chat/completions", "/v1/responses"], requests.Select(r => r.Path));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0,7")]
    public async Task Temperature_RejectsInvalidValuesForDefaultAndProfiles(string value)
    {
        using var client = ModelsClient();
        var host = new TestPluginHostServices { Localization = TimeoutLocalization() };
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("temperature", value));
        var expected = host.Localization.GetString("Settings.TemperatureInvalid");
        Assert.Equal(expected, ex.Message);
        Assert.Equal("0.3", await sut.GetSettingValueAsync("temperature"));
        var result = await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", temperature: value)]);
        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Message);
        Assert.Empty(await sut.GetItemsAsync("profiles"));
    }

    [Theory]
    [InlineData(false, "<think>plan</think> answer ", "answer")]
    [InlineData(true, "<think>plan</think> answer ", "answer")]
    [InlineData(false, "<think>plan</think>", null)]
    [InlineData(true, "<think>plan</think>", null)]
    public async Task ResponsesEndpoint_StripsThinkBlocks(bool useProfile, string outputText, string? expected)
    {
        using var client = new HttpClient(new CapturingHandler((request, _) => request.Method == HttpMethod.Get
            ? ModelCatalogResponse("m1")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { output_text = outputText })),
            }));
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://example.test");
        host.SetSetting("textApi", "responses");
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        ILlmProviderRole role = sut;
        if (useProfile)
        {
            Assert.True((await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", llmModel: "m1", textApi: "responses")])).IsSuccess);
            role = Assert.Single(sut.AdditionalLlmProviders);
        }
        if (expected is not null)
        {
            Assert.Equal(expected, await role.ProcessAsync("system", "user", "m1", CancellationToken.None));
            return;
        }
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => role.ProcessAsync("system", "user", "m1", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.EmptyResponse, ex.FailureKind);
    }

    [Fact]
    public async Task AdditionalProfile_BlankTemperatureKeepsPreviousOrDefault()
    {
        // The settings UI seeds new text fields with "" (see PluginCollectionViewModels.AddItem).
        using var client = ModelsClient();
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices());
        var result = await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", temperature: "")]);
        Assert.True(result.IsSuccess, result.Message);
        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("0.3", item.Values["temperature"]);
        Assert.True((await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", id: item.Values["__id"],
            temperatureMode: "custom", temperature: "0.7")])).IsSuccess);
        Assert.True((await sut.SetItemsAsync("profiles", [ProfileItem("P", "https://example.test", id: item.Values["__id"],
            temperatureMode: "custom", temperature: " ")])).IsSuccess);
        Assert.Equal("0.7", Assert.Single(await sut.GetItemsAsync("profiles")).Values["temperature"]);
    }

    [Fact]
    public async Task TextOptions_DefaultsNormalizeAndPersist()
    {
        using var client = ModelsClient();
        var host = new TestPluginHostServices();
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var defaults = new Dictionary<string, string>
        {
            ["textApi"] = "chat-completions", ["reasoningEffort"] = "",
            ["temperatureMode"] = "provider-default", ["temperature"] = "0.3",
        };
        foreach (var (key, value) in defaults)
            Assert.Equal(value, await sut.GetSettingValueAsync(key));
        foreach (var key in defaults.Keys.Where(k => k != "temperature"))
        {
            await sut.SetSettingValueAsync(key, "unknown");
            Assert.Equal(defaults[key], await sut.GetSettingValueAsync(key));
            Assert.Equal(defaults[key], host.GetSetting<string>(key));
        }
        await sut.SetSettingValueAsync("textApi", "responses");
        await sut.SetSettingValueAsync("reasoningEffort", "max");
        await sut.SetSettingValueAsync("temperatureMode", "custom");
        await sut.SetSettingValueAsync("temperature", "1.5");
        var reloaded = new OpenAiCompatiblePlugin(client);
        await reloaded.ActivateAsync(host);
        Assert.Equal("responses", await reloaded.GetSettingValueAsync("textApi"));
        Assert.Equal("max", await reloaded.GetSettingValueAsync("reasoningEffort"));
        Assert.Equal("custom", await reloaded.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("1.5", await reloaded.GetSettingValueAsync("temperature"));
        var fields = Assert.Single(sut.GetCollectionDefinitions()).ItemFields;
        foreach (var key in defaults.Keys)
        {
            var flat = Assert.Single(sut.GetSettingDefinitions(), d => d.Key == key);
            var collection = Assert.Single(fields, d => d.Key == key);
            Assert.Equal(key == "temperature" ? PluginSettingKind.Text : PluginSettingKind.Dropdown, flat.Kind);
            Assert.Equal(flat.Label, collection.Label);
            Assert.Equal(flat.Description, collection.Description);
            Assert.Equal(flat.Options, collection.Options);
        }
    }

    [Fact]
    public async Task AdditionalProfile_UnknownTextOptionsLoadWithDefaults()
    {
        using var client = ModelsClient();
        var host = new TestPluginHostServices();
        host.SetSetting("additionalProfiles", new List<OpenAiCompatibleProfile>
        {
            new() { Id = "openai-compatible-test", Name = "P", BaseUrl = "https://example.test",
                TextApi = "unknown", ReasoningEffort = "unknown", TemperatureMode = "unknown", Temperature = 2.5 },
        });
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var saved = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("chat-completions", saved.Values["textApi"]);
        Assert.Equal("", saved.Values["reasoningEffort"]);
        Assert.Equal("provider-default", saved.Values["temperatureMode"]);
        Assert.Equal("0.3", saved.Values["temperature"]);
        Assert.Equal(0.3, Assert.Single(host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!).Temperature);
    }

    private static HttpClient TextOptionsClient(List<(string Path, JsonElement Body)> requests) =>
        new(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return ModelCatalogResponse("m1");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer key", request.Headers.Authorization?.ToString());
            using var doc = JsonDocument.Parse(body!);
            requests.Add((request.RequestUri!.AbsolutePath, doc.RootElement.Clone()));
            var responses = request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal);
            var streaming = doc.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses ? """{"output_text":"response"}"""
                    : streaming ? "data: {\"choices\":[{\"delta\":{\"content\":\"chat\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                    : """{"choices":[{"message":{"content":"chat"}}]}"""),
            };
        }));

    private static async Task<List<string>> ProcessTextOptionsAsync(ILlmProviderRole role, bool streaming)
    {
        if (!streaming)
            return [await role.ProcessAsync("system", "user", "m1", CancellationToken.None)];
        var chunks = new List<string>();
        await foreach (var chunk in role.ProcessStreamingAsync("system", "user", "m1", CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshModelCatalogAsync_DeadlineRetainsCacheAndContinuesToNextProfile(bool defaultTimesOut)
    {
        var host = CachedProfileHost();
        var profiles = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        profiles.Add(new OpenAiCompatibleProfile
        {
            Id = "next-profile", Name = "Next", BaseUrl = "http://localhost:8888",
            SelectedModelId = "old", SelectedLlmModelId = "old",
            FetchedModels = [new FetchedModel("old", null)],
        });
        host.SetSetting("additionalProfiles", profiles);
        if (defaultTimesOut)
        {
            host.SetSetting("baseUrl", "http://localhost:7777");
            host.SetSetting("fetchedModels", JsonSerializer.Serialize(new List<FetchedModel> { new("default-old", null) }));
        }
        using var client = new HttpClient(new AsyncHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Port == (defaultTimesOut ? 7777 : 9999))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"new"}]}"""),
            };
        }));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var sut = new OpenAiCompatiblePlugin(client, TimeSpan.FromMilliseconds(100));
        await sut.ActivateAsync(host);
        var notifications = host.CapabilitiesChangedCount;

        await sut.RefreshModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var saved = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        Assert.Equal(defaultTimesOut ? "new" : "m1", saved[0].SelectedModelId);
        Assert.Equal(defaultTimesOut ? "new" : "m1", Assert.Single(saved[0].FetchedModels).Id);
        Assert.Equal("new", saved[1].SelectedModelId);
        if (defaultTimesOut)
            Assert.Equal("default-old", Assert.Single(sut.FetchedModels).Id);
        Assert.Equal(notifications + 1, host.CapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetItemsAsync_CatalogDeadlineStillNotifiesAndRefreshesNextProfile()
    {
        var host = new TestPluginHostServices();
        using var client = new HttpClient(new AsyncHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Port == 9999)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"new"}]}"""),
            };
        }));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var sut = new OpenAiCompatiblePlugin(client, TimeSpan.FromMilliseconds(100));
        await sut.ActivateAsync(host);
        var notifications = host.CapabilitiesChangedCount;

        var result = await sut.SetItemsAsync("profiles",
            [ProfileItem("Timeout", "http://localhost:9999"), ProfileItem("Next", "http://localhost:8888")])
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        var saved = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        Assert.Empty(saved[0].FetchedModels);
        Assert.Equal("new", Assert.Single(saved[1].FetchedModels).Id);
        Assert.Equal(notifications + 1, host.CapabilitiesChangedCount);
    }

    private static PluginLocalization TimeoutLocalization() => new(
        Path.GetFullPath(Path.Join("..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.OpenAiCompatible"),
            AppContext.BaseDirectory), "de");

    [Theory]
    [InlineData("transcription", false)]
    [InlineData("profile", false)]
    [InlineData("validation", false)]
    [InlineData("catalog", false)]
    [InlineData("transcription", true)]
    [InlineData("profile", true)]
    [InlineData("validation", true)]
    [InlineData("catalog", true)]
    public async Task NonLlmTimeout_DeadlineAndCallerCancellationHaveDistinctExceptions(string operation, bool cancelCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHandler(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var sut = new OpenAiCompatiblePlugin(client,
            cancelCaller ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        var host = operation == "profile" ? CachedProfileHost() : CachedDefaultHost();
        host.Localization = TimeoutLocalization();
        await sut.ActivateAsync(host);
        using var caller = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task request = operation switch
        {
            "profile" => sut.AdditionalTranscriptionEngines[0].TranscribeAsync([0], "en", false, null, caller.Token),
            "validation" => sut.ValidateConnectionAsync(caller.Token),
            "catalog" => sut.FetchModelsAsync(caller.Token),
            _ => sut.TranscribeAsync([0], "en", false, null, caller.Token),
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), guard.Token);
        if (cancelCaller)
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request).WaitAsync(TimeSpan.FromSeconds(10), guard.Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => request).WaitAsync(TimeSpan.FromSeconds(10), guard.Token);
            Assert.Equal(host.Localization.GetString("Settings.RequestTimedOut"), error.Message);
        }
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(7200, 3600)]
    [InlineData(120, 120)]
    public async Task LlmTimeout_ClampsAndPersistsProfileSetting(int input, int expected)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") }));
        var host = new TestPluginHostServices();
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var item = ProfileItem("P", "http://localhost:11434", llmModel: "m1");
        var values = item.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["llmRequestTimeoutSeconds"] = input.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await sut.SetItemsAsync("profiles", [new PluginCollectionItem(values)]);
        Assert.True(result.IsSuccess);
        var saved = Assert.Single(host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!);
        Assert.Equal(expected, saved.LlmRequestTimeoutSeconds);
        using var restored = new OpenAiCompatiblePlugin(client);
        await restored.ActivateAsync(host);
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(await restored.GetItemsAsync("profiles")).Values["llmRequestTimeoutSeconds"]);
    }

    [Fact]
    public void LlmTimeout_DefaultsToFiveMinutes()
    {
        Assert.Equal(300, new OpenAiCompatibleProfile().LlmRequestTimeoutSeconds);
        Assert.Equal(5, JsonSerializer.Deserialize<OpenAiCompatibleProfile>("{\"LlmRequestTimeoutSeconds\":-1}")!.LlmRequestTimeoutSeconds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false, "responses")]
    [InlineData(false, false, true, "responses")]
    [InlineData(false, true, false, "responses")]
    [InlineData(false, true, true, "responses")]
    [InlineData(true, false, false, "responses")]
    [InlineData(true, false, true, "responses")]
    [InlineData(true, true, false, "responses")]
    [InlineData(true, true, true, "responses")]
    public async Task LlmTimeout_DeadlineAndCallerCancellationHaveDistinctExceptions(bool streaming, bool cancelCaller, bool useDefault, string textApi = "chat-completions")
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var body = new StalledSseStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n");
        var chunks = new List<string>();
        using var client = new HttpClient(new AsyncHandler(async (_, ct) =>
        {
            if (streaming && textApi == "chat-completions")
            {
                // ReSharper disable once AccessToDisposedClosure -- the handler completes before the stream is disposed at test exit.
                var content = new StreamContent(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        client.Timeout = Timeout.InfiniteTimeSpan;
        var host = CachedProfileHost();
        host.Localization = TimeoutLocalization();
        var profiles = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        profiles[0].LlmRequestTimeoutSeconds = 5;
        profiles[0].TextApi = textApi;
        host.SetSetting("textApi", textApi);
        host.SetSetting("additionalProfiles", profiles);
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting("llmRequestTimeoutSeconds", 5);
        using var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        using var caller = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var role = useDefault ? sut : sut.AdditionalLlmProviders[0];
        var request = RequestAsync(caller.Token);
        await (streaming && textApi == "chat-completions" ? body.Stalled.Task : entered.Task).WaitAsync(TimeSpan.FromSeconds(10), guard.Token);
        if (streaming && textApi == "chat-completions")
            Assert.Equal(["Hel"], chunks);
        if (cancelCaller)
        {
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request).WaitAsync(TimeSpan.FromSeconds(15), guard.Token);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => request).WaitAsync(TimeSpan.FromSeconds(15), guard.Token);
            Assert.Equal(host.Localization.GetString("Settings.LlmRequestTimedOut"), error.Message);
        }
        return;

        async Task RequestAsync(CancellationToken ct)
        {
            if (streaming)
            {
                await foreach (var chunk in role.ProcessStreamingAsync("system", "text", "m1", ct))
                    chunks.Add(chunk);
            }
            else
                await role.ProcessAsync("system", "text", "m1", ct);
        }
    }


    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<OpenAiCompatiblePlugin>();


    [Fact]
    public async Task ProcessAsync_LongInput_KeepsFixedOutputCap()
    {
        var longInput = string.Concat(Enumerable.Repeat("dictated input ", 1_000));
        var body = await CaptureThinkingRequestAsync("default", userText: longInput);
        Assert.Equal(2048, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ProcessAsync_ThinkingModeDefault_SendsNoThinkingControl()
    {
        var body = await CaptureThinkingRequestAsync("default");
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task ProcessAsync_ThinkingModeOff_SendsThinkingDisabled()
    {
        var body = await CaptureThinkingRequestAsync("off");
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ProcessAsync_ThinkingModeOn_SendsThinkingEnabled()
    {
        var body = await CaptureThinkingRequestAsync("on");
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.True(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData("off", "none")]
    [InlineData("on", "high")]
    public async Task ProcessAsync_DeepInfra_UsesReasoningEffort(string mode, string expected)
    {
        var body = await CaptureThinkingRequestAsync(mode, "https://API.DEEPINFRA.COM/custom-path");
        Assert.Equal(expected, body.GetProperty("reasoning_effort").GetString());
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task ProcessAsync_DeepInfraLookalikeHost_UsesGenericThinkingControl()
    {
        var body = await CaptureThinkingRequestAsync("off", "https://api.deepinfra.com.example.org");
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThinkingModeOff_SendsThinkingDisabled()
    {
        var body = await CaptureThinkingRequestAsync("off", streaming: true);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ThinkingMode_RoundTripsThroughSettingsProvider()
    {
        var host = new TestPluginHostServices();
        using var client = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("thinkingMode", "on");
        Assert.Equal("on", await sut.GetSettingValueAsync("thinkingMode"));
        Assert.Equal("on", host.GetSetting<string>("thinkingMode"));
        Assert.Contains("thinkingMode", host.SettingWrites);
        await sut.SetSettingValueAsync("thinkingMode", "unknown");
        Assert.Equal("default", await sut.GetSettingValueAsync("thinkingMode"));
        Assert.Equal("default", host.GetSetting<string>("thinkingMode"));
        var definition = Assert.Single(sut.GetSettingDefinitions(), d => d.Key == "thinkingMode");
        Assert.Equal(PluginSettingKind.Dropdown, definition.Kind);
        Assert.Equal(["default", "off", "on"], definition.Options!.Select(o => o.Value));
    }

    [Fact]
    public async Task ActivateAsync_WithoutThinkingModeSetting_DefaultsToProviderDefault()
    {
        using var client = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices());
        Assert.Equal("default", await sut.GetSettingValueAsync("thinkingMode"));
    }

    [Fact]
    public async Task ProcessAsync_ThrowsWhenResponseContainsOnlyReasoningContent()
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"reasoning_content":"plan"}}]}"""),
        }));
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "https://example.test");
        var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ProcessAsync("system", "user", "model", CancellationToken.None));
        Assert.Contains("content", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditionalProfile_ThinkingMode_PersistsAndAppliesToRequests(bool streaming)
    {
        string? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? "data: [DONE]\n\n"
                    : """{"choices":[{"message":{"content":"ok"}}]}"""),
            };
        }));
        var host = new TestPluginHostServices();
        var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        var item = new PluginCollectionItem(new Dictionary<string, string?>
        {
            ["name"] = "Thinking endpoint", ["baseUrl"] = "https://example.test",
            ["selectedLlmModel"] = "model", ["thinkingMode"] = "off",
        });
        var definition = Assert.Single(sut.GetCollectionDefinitions()).ItemFields
            .Single(d => d.Key == "thinkingMode");
        Assert.Equal(PluginSettingKind.Dropdown, definition.Kind);
        await sut.SetItemsAsync("profiles", [item]);
        var reloaded = new OpenAiCompatiblePlugin(client);
        await reloaded.ActivateAsync(host);
        var saved = Assert.Single(await reloaded.GetItemsAsync("profiles"));
        Assert.Equal("off", saved.Values["thinkingMode"]);
        var role = Assert.Single(reloaded.AdditionalLlmProviders);
        if (streaming)
        {
            await foreach (var chunk in role.ProcessStreamingAsync("system", "user", "model", CancellationToken.None))
                Assert.Fail($"Unexpected delta: {chunk}");
        }
        else
            Assert.Equal("ok", await role.ProcessAsync("system", "user", "model", CancellationToken.None));
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("disabled", doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    private static async Task<JsonElement> CaptureThinkingRequestAsync(
        string mode, string baseUrl = "https://example.test", bool streaming = false, string userText = "user")
    {
        string? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? "data: [DONE]\n\n"
                    : """{"choices":[{"message":{"content":"ok"}}]}"""),
            };
        }));
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", baseUrl);
        host.SetSetting("thinkingMode", mode);
        var sut = new OpenAiCompatiblePlugin(client);
        await sut.ActivateAsync(host);
        if (streaming)
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("system", userText, "model", CancellationToken.None))
                Assert.Fail($"Unexpected delta: {chunk}");
        }
        else
            await sut.ProcessAsync("system", userText, "model", CancellationToken.None);
        using var doc = JsonDocument.Parse(capturedBody!);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsDeltas_AgainstOpenAiCompatibleServer()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
        Assert.Equal("http://localhost:11434/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("llama3", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"bulk"}}]}""",
                Encoding.UTF8, "application/json"),
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    private static HttpClient ModelsClient() =>
        new(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"m1"},{"id":"m2"}]}""",
                Encoding.UTF8, "application/json"),
        }));

    private static HttpResponseMessage ModelCatalogResponse(params string[] modelIds) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    data = modelIds.Select(id => new { id }),
                }),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static PluginCollectionItem ProfileItem(
        string name, string baseUrl, string? apiKey = null,
        string? model = null, string? llmModel = null, string? id = "",
        string textApi = "chat-completions", string reasoningEffort = "",
        string temperatureMode = "provider-default", string temperature = "0.3",
        string apiVersion = "", string batchEndpoint = "standard") =>
        new(new Dictionary<string, string?>
        {
            ["name"] = name,
            ["baseUrl"] = baseUrl,
            ["api-key"] = apiKey,
            ["selectedModel"] = model,
            ["selectedLlmModel"] = llmModel,
            ["__id"] = id,
            ["apiVersion"] = apiVersion,
            ["batchEndpoint"] = batchEndpoint,
            ["textApi"] = textApi,
            ["reasoningEffort"] = reasoningEffort,
            ["temperatureMode"] = temperatureMode,
            ["temperature"] = temperature,
        });

    [Fact]
    public async Task ActivateAsync_WhitespaceLegacyProfileId_PersistsStableRepairAndSelections()
    {
        // Pre-fix, each activation generated a different in-memory ID because the
        // repaired ID was never saved, invalidating profile selection identities.
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "   ",
                    Name = "Legacy",
                    BaseUrl = "http://localhost:11434",
                    SelectedModelId = "stt-model",
                    SelectedLlmModelId = "llm-model",
                    FetchedModels =
                    [
                        new FetchedModel("stt-model", null),
                        new FetchedModel("llm-model", null),
                    ],
                },
            }
        );
        host.SettingWrites.Clear();
        using var httpClient = ModelsClient();

        var first = new OpenAiCompatiblePlugin(httpClient);
        await first.ActivateAsync(host);
        var firstItem = Assert.Single(await first.GetItemsAsync("profiles"));
        var repairedId = Assert.IsType<string>(firstItem.Values["__id"]);

        Assert.StartsWith("openai-compatible-", repairedId);
        Assert.Equal("stt-model", firstItem.Values["selectedModel"]);
        Assert.Equal("llm-model", firstItem.Values["selectedLlmModel"]);
        Assert.Equal(
            repairedId,
            Assert.Single(first.AdditionalTranscriptionEngines).GetTranscriptionSelectionId()
        );

        var persisted = Assert.Single(
            host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!
        );
        Assert.Equal(repairedId, persisted.Id);

        var second = new OpenAiCompatiblePlugin(httpClient);
        await second.ActivateAsync(host);
        var secondItem = Assert.Single(await second.GetItemsAsync("profiles"));

        Assert.Equal(repairedId, secondItem.Values["__id"]);
        Assert.Equal("stt-model", secondItem.Values["selectedModel"]);
        Assert.Equal("llm-model", secondItem.Values["selectedLlmModel"]);
        Assert.Equal(
            repairedId,
            Assert.Single(second.AdditionalTranscriptionEngines).GetTranscriptionSelectionId()
        );
        Assert.Equal(
            1,
            host.SettingWrites.Count(key => key == "additionalProfiles")
        );
    }

    [Fact]
    public async Task ActivateAsync_DuplicateProfileId_RepairsLoserWithoutMovingKeeperSecret()
    {
        // Pre-fix, the duplicate loser received a transient ID that was regenerated
        // on every activation because the repaired profile list was not persisted.
        const string sharedId = "openai-compatible-shared";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = sharedId,
                    Name = "Keeper",
                    BaseUrl = "http://localhost:11434",
                },
                new()
                {
                    Id = sharedId,
                    Name = "Duplicate",
                    BaseUrl = "http://localhost:11435",
                },
            }
        );
        host.Secrets[$"api-key.{sharedId}"] = "keeper-secret";
        host.SettingWrites.Clear();
        var authorizationHeaders = new List<string?>();
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var items = await sut.GetItemsAsync("profiles");
        Assert.Equal(2, items.Count);
        var keeperId = Assert.IsType<string>(items[0].Values["__id"]);
        var repairedLoserId = Assert.IsType<string>(items[1].Values["__id"]);
        Assert.Equal(sharedId, keeperId);
        Assert.NotEqual(sharedId, repairedLoserId);
        Assert.StartsWith("openai-compatible-", repairedLoserId);

        var persisted = host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!;
        Assert.Equal([keeperId, repairedLoserId], persisted.Select(profile => profile.Id));
        Assert.Equal("keeper-secret", host.Secrets[$"api-key.{sharedId}"]);
        Assert.Empty(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);

        await sut.ProcessForProfileAsync(keeperId, "system", "user", "m1", CancellationToken.None);
        await sut.ProcessForProfileAsync(
            repairedLoserId,
            "system",
            "user",
            "m1",
            CancellationToken.None
        );
        Assert.Equal(["Bearer keeper-secret", "Bearer"], authorizationHeaders);
        Assert.Equal(
            1,
            host.SettingWrites.Count(key => key == "additionalProfiles")
        );
    }

    [Fact]
    public async Task ActivateAsync_InvalidLegacyProfileId_MigratesSecretAndUsesIt()
    {
        // Pre-fix, activation looked only under the generated ID, leaving the legacy
        // secret orphaned and profile requests unauthenticated.
        const string oldId = "legacy:server";
        const string oldSecretKey = "api-key.legacy:server";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = oldId,
                    Name = "Legacy",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[oldSecretKey] = "legacy-secret";
        host.SettingWrites.Clear();
        string? authorizationHeader = null;
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var repairedId = Assert.IsType<string>(item.Values["__id"]);
        var newSecretKey = $"api-key.{repairedId}";
        Assert.Equal("legacy-secret", host.Secrets[newSecretKey]);
        Assert.False(host.Secrets.ContainsKey(oldSecretKey));
        Assert.Equal(
            [("store", newSecretKey), ("delete", oldSecretKey)],
            host.SecretOperations
        );

        var result = await sut.ProcessForProfileAsync(
            repairedId,
            "system",
            "user",
            "m1",
            CancellationToken.None
        );
        Assert.Equal("ok", result);
        Assert.Equal("Bearer legacy-secret", authorizationHeader);
    }

    [Fact]
    public async Task ActivateAsync_DuplicateInvalidLegacyProfileIds_MigratesSecretToFirstProfileOnly()
    {
        // One legacy secret cannot belong to two servers: copying it to both repaired
        // profiles would disclose the credential to the second profile's base URL.
        const string oldId = "legacy:server";
        const string oldSecretKey = "api-key.legacy:server";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = oldId,
                    Name = "First",
                    BaseUrl = "http://localhost:11434",
                },
                new()
                {
                    Id = oldId,
                    Name = "Second",
                    BaseUrl = "http://evil.example.com",
                },
            }
        );
        host.Secrets[oldSecretKey] = "legacy-secret";
        host.SettingWrites.Clear();
        var authorizationHeaders = new List<string?>();
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var items = await sut.GetItemsAsync("profiles");
        Assert.Equal(2, items.Count);
        var firstId = Assert.IsType<string>(items[0].Values["__id"]);
        var secondId = Assert.IsType<string>(items[1].Values["__id"]);
        Assert.NotEqual(firstId, secondId);

        Assert.Equal(
            [("store", $"api-key.{firstId}"), ("delete", oldSecretKey)],
            host.SecretOperations
        );
        Assert.Equal("legacy-secret", host.Secrets[$"api-key.{firstId}"]);
        Assert.False(host.Secrets.ContainsKey($"api-key.{secondId}"));

        await sut.ProcessForProfileAsync(firstId, "system", "user", "m1", CancellationToken.None);
        await sut.ProcessForProfileAsync(secondId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal(["Bearer legacy-secret", "Bearer"], authorizationHeaders);
    }

    [Fact]
    public async Task ActivateAsync_PaddedDuplicateProfileId_MigratesItsOwnDistinctSecret()
    {
        // The padded duplicate stores its secret under its own raw key, so suppressing
        // the migration would orphan a credential the keeper never owned.
        const string keeperId = "openai-compatible-a";
        const string paddedSecretKey = "api-key.  openai-compatible-a  ";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = keeperId,
                    Name = "Keeper",
                    BaseUrl = "http://localhost:11434",
                },
                new()
                {
                    Id = "  openai-compatible-a  ",
                    Name = "Padded",
                    BaseUrl = "http://localhost:11435",
                },
            }
        );
        host.Secrets[$"api-key.{keeperId}"] = "keeper-secret";
        host.Secrets[paddedSecretKey] = "padded-secret";
        host.SettingWrites.Clear();
        var authorizationHeaders = new List<string?>();
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var items = await sut.GetItemsAsync("profiles");
        Assert.Equal(2, items.Count);
        Assert.Equal(keeperId, items[0].Values["__id"]);
        var repairedId = Assert.IsType<string>(items[1].Values["__id"]);
        Assert.NotEqual(keeperId, repairedId);

        Assert.Equal("keeper-secret", host.Secrets[$"api-key.{keeperId}"]);
        Assert.Equal("padded-secret", host.Secrets[$"api-key.{repairedId}"]);
        Assert.False(host.Secrets.ContainsKey(paddedSecretKey));
        Assert.Equal(
            [("store", $"api-key.{repairedId}"), ("delete", paddedSecretKey)],
            host.SecretOperations
        );

        await sut.ProcessForProfileAsync(keeperId, "system", "user", "m1", CancellationToken.None);
        await sut.ProcessForProfileAsync(repairedId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal(["Bearer keeper-secret", "Bearer padded-secret"], authorizationHeaders);
    }

    [Fact]
    public async Task ActivateAsync_PaddedIdBeforeExactHolder_LeavesExactSecretWithItsOwner()
    {
        // The padded entry comes first but must not normalize onto an ID a later
        // profile stores exactly: that would send the exact profile's credential to
        // the padded profile's base URL and persist the mix-up.
        const string exactId = "openai-compatible-a";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "  openai-compatible-a  ",
                    Name = "Padded",
                    BaseUrl = "http://padded.example.com",
                },
                new()
                {
                    Id = exactId,
                    Name = "Exact",
                    BaseUrl = "http://exact.example.com",
                },
            }
        );
        host.Secrets[$"api-key.{exactId}"] = "exact-secret";
        host.SettingWrites.Clear();
        var requests = new List<(string? Host, string? Authorization)>();
        var handler = new CapturingHandler((request, _) =>
        {
            requests.Add((request.RequestUri?.Host, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var items = await sut.GetItemsAsync("profiles");
        Assert.Equal(2, items.Count);
        var paddedId = Assert.IsType<string>(items[0].Values["__id"]);
        Assert.NotEqual(exactId, paddedId);
        Assert.Equal(exactId, items[1].Values["__id"]);
        Assert.Equal("exact-secret", host.Secrets[$"api-key.{exactId}"]);

        await sut.ProcessForProfileAsync(paddedId, "system", "user", "m1", CancellationToken.None);
        await sut.ProcessForProfileAsync(exactId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal(
            [("padded.example.com", "Bearer"), ("exact.example.com", "Bearer exact-secret")],
            requests
        );
    }

    [Fact]
    public async Task ActivateAsync_NullLegacyProfileId_MigratesItsSecret()
    {
        // A missing ID in persisted JSON addresses "api-key." exactly as an empty ID
        // does, so it has to migrate on the same terms.
        const string nullIdSecretKey = "api-key.";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = null!,
                    Name = "No Id",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[nullIdSecretKey] = "orphan-secret";
        host.Secrets["api-key"] = "default-endpoint-secret";
        host.SettingWrites.Clear();
        string? authorizationHeader = null;
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var repairedId = Assert.IsType<string>(item.Values["__id"]);
        Assert.Equal("orphan-secret", host.Secrets[$"api-key.{repairedId}"]);
        Assert.False(host.Secrets.ContainsKey(nullIdSecretKey));
        Assert.Equal("default-endpoint-secret", host.Secrets["api-key"]);

        await sut.ProcessForProfileAsync(repairedId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal("Bearer orphan-secret", authorizationHeader);
    }

    [Fact]
    public async Task ActivateAsync_BlankLegacyProfileId_MigratesItsSecret()
    {
        // A blank ID still addresses a secret key of its own; the repair renames the
        // profile, so the credential has to move with it.
        const string blankSecretKey = "api-key.   ";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "   ",
                    Name = "Blank",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[blankSecretKey] = "blank-secret";
        host.Secrets["api-key"] = "default-endpoint-secret";
        host.SettingWrites.Clear();
        string? authorizationHeader = null;
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var repairedId = Assert.IsType<string>(item.Values["__id"]);
        Assert.Equal("blank-secret", host.Secrets[$"api-key.{repairedId}"]);
        Assert.False(host.Secrets.ContainsKey(blankSecretKey));

        // The default endpoint's own secret lives at "api-key" and must be untouched.
        Assert.Equal("default-endpoint-secret", host.Secrets["api-key"]);
        Assert.DoesNotContain("api-key", host.DeletedSecretKeys);

        await sut.ProcessForProfileAsync(repairedId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal("Bearer blank-secret", authorizationHeader);
    }

    [Fact]
    public async Task ActivateAsync_PaddedLegacyProfileId_KeepsExistingDestinationSecret()
    {
        // The canonical key already holds the credential this profile has been using;
        // the padded legacy entry is the stale copy and must not overwrite it.
        const string canonicalId = "openai-compatible-a";
        const string paddedSecretKey = "api-key.  openai-compatible-a  ";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "  openai-compatible-a  ",
                    Name = "Padded",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[$"api-key.{canonicalId}"] = "live-secret";
        host.Secrets[paddedSecretKey] = "stale-secret";
        host.SettingWrites.Clear();
        string? authorizationHeader = null;
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal(canonicalId, item.Values["__id"]);
        Assert.Equal("live-secret", host.Secrets[$"api-key.{canonicalId}"]);
        Assert.Equal("stale-secret", host.Secrets[paddedSecretKey]);
        Assert.Empty(host.SecretOperations);

        await sut.ProcessForProfileAsync(
            canonicalId,
            "system",
            "user",
            "m1",
            CancellationToken.None
        );
        Assert.Equal("Bearer live-secret", authorizationHeader);
    }

    [Fact]
    public async Task ActivateAsync_LegacySecretDeleteFailure_StillActivatesWithMigratedSecret()
    {
        // Retiring the old key is cleanup after the migration is already durable, so
        // a delete failure must not fail activation or strand the repaired profile.
        const string oldSecretKey = "api-key.legacy:server";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "legacy:server",
                    Name = "Legacy",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[oldSecretKey] = "legacy-secret";
        host.SettingWrites.Clear();
        host.FailDeleteSecretWrites = true;
        string? authorizationHeader = null;
        var handler = new CapturingHandler((request, _) =>
        {
            authorizationHeader = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"ok"}}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        var repairedId = Assert.IsType<string>(item.Values["__id"]);
        Assert.Equal("legacy-secret", host.Secrets[$"api-key.{repairedId}"]);
        Assert.Equal("legacy-secret", host.Secrets[oldSecretKey]);
        Assert.Equal(
            repairedId,
            Assert.Single(host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!).Id
        );

        await sut.ProcessForProfileAsync(repairedId, "system", "user", "m1", CancellationToken.None);
        Assert.Equal("Bearer legacy-secret", authorizationHeader);
    }

    [Fact]
    public async Task ActivateAsync_MigrationStoreFailureAfterEarlierMigration_KeepsEveryLegacySecret()
    {
        // A mid-migration failure must not retire any legacy key: the still-persisted
        // legacy IDs stay the addressable copies, so the next activation can retry.
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "legacy:a",
                    Name = "A",
                    BaseUrl = "http://localhost:11434",
                },
                new()
                {
                    Id = "legacy:b",
                    Name = "B",
                    BaseUrl = "http://localhost:11435",
                },
            }
        );
        host.Secrets["api-key.legacy:a"] = "secret-a";
        host.Secrets["api-key.legacy:b"] = "secret-b";
        host.SettingWrites.Clear();
        host.StoreSecretFailAfter = 1;
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await Assert.ThrowsAsync<IOException>(() => sut.ActivateAsync(host));

        Assert.Equal("secret-a", host.Secrets["api-key.legacy:a"]);
        Assert.Equal("secret-b", host.Secrets["api-key.legacy:b"]);
        Assert.Empty(host.DeletedSecretKeys);
        Assert.Empty(host.SettingWrites);
        Assert.Equal(
            ["legacy:a", "legacy:b"],
            host.GetSetting<List<OpenAiCompatibleProfile>>("additionalProfiles")!
                .Select(profile => profile.Id)
        );
    }

    [Fact]
    public async Task ActivateAsync_MigrationStoreFailure_PreservesOldSecret()
    {
        // Pre-fix, no migration store was attempted at all, so activation did not
        // surface the failure and the repaired profile silently lost access to its key.
        const string oldSecretKey = "api-key.legacy:server";
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "legacy:server",
                    Name = "Legacy",
                    BaseUrl = "http://localhost:11434",
                },
            }
        );
        host.Secrets[oldSecretKey] = "legacy-secret";
        host.SettingWrites.Clear();
        host.FailStoreSecretWrites = true;
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await Assert.ThrowsAsync<IOException>(() => sut.ActivateAsync(host));

        Assert.Equal("legacy-secret", host.Secrets[oldSecretKey]);
        Assert.Single(host.Secrets);
        Assert.Empty(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);
        Assert.Empty(host.SettingWrites);
    }

    [Fact]
    public async Task ActivateAsync_ValidUniqueProfileIds_DoesNotRewriteProfiles()
    {
        // Pre-fix, a clean load also performed no settings write; this guard ensures
        // repair persistence remains conditional and preserves that behavior.
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "openai-compatible-one",
                    Name = "One",
                    BaseUrl = "http://localhost:11434",
                },
                new()
                {
                    Id = "openai-compatible-two",
                    Name = "Two",
                    BaseUrl = "http://localhost:11435",
                },
            }
        );
        host.SettingWrites.Clear();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        Assert.Empty(host.SettingWrites);
    }

    [Fact]
    public async Task SetBaseUrl_EndpointChange_InvalidatesStateThenRefreshSelectsFirstModel()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"new-model"},{"id":"new-model-2"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("baseUrl", "http://localhost:9999");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Empty(
            JsonSerializer.Deserialize<List<FetchedModel>>(
                host.GetSetting<string>("fetchedModels")!
            )!
        );
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));

        await sut.RefreshModelCatalogAsync();

        Assert.Equal(["new-model", "new-model-2"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("new-model", sut.SelectedTranscriptionModelId);
        Assert.Equal("new-model", sut.SelectedLlmModelId);
        Assert.Equal("new-model", host.GetSetting<string>("selectedModel"));
        Assert.Equal("new-model", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task SetApiKeyAsync_CredentialChange_InvalidatesDefaultCatalogAndSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("new-key");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task FullFormSave_BaseUrlChange_DoesNotRestoreStaleSelectionsFromLaterFields()
    {
        // Reproduces the host's full-form save (TrySaveFlatSettingsAsync), which applies
        // every field in definition order. A base-URL change clears the catalog and both
        // selections, but the form still carries the old selectedModel/selectedLlmModel in
        // the later fields; those setters must not re-pair the new endpoint with stale IDs.
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "old-model");
        host.SetSetting("selectedLlmModel", "old-model");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("old-model", null) })
        );
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        // Host save order: baseUrl, api-key, selectedModel, selectedLlmModel.
        await sut.SetSettingValueAsync("baseUrl", "http://localhost:9999");
        await sut.SetSettingValueAsync("api-key", "");
        await sut.SetSettingValueAsync("selectedModel", "old-model");
        await sut.SetSettingValueAsync("selectedLlmModel", "old-model");

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task SetItemsAsync_AddsProfile_ExposesRoleWithProfileSelectionId()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123", llmModel: "m1")]);

        Assert.True(result.IsSuccess);

        var llm = Assert.Single(sut.AdditionalLlmProviders);
        Assert.Equal("Local Ollama", llm.ProviderName);
        Assert.True(llm.IsAvailable);

        var selectionId = llm.GetLlmSelectionId();
        Assert.StartsWith("openai-compatible-", selectionId);
        Assert.DoesNotContain(":", selectionId); // must round-trip in plugin:{id}:{model}

        var engine = Assert.Single(sut.AdditionalTranscriptionEngines);
        Assert.Equal(selectionId, engine.GetTranscriptionSelectionId());
        Assert.Equal(sut.PluginId, engine.PluginId); // role keeps the owner's plugin id
    }

    [Fact]
    public async Task SetItemsAsync_WhenASecretWriteFailsMidUpdate_RollsBackTheEarlierOnes()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem("First", "http://localhost:11434", apiKey: "first-key"),
                ProfileItem("Second", "http://localhost:11435", apiKey: "second-key"),
            ]
        );

        var saved = await sut.GetItemsAsync("profiles");
        var firstId = saved[0].Values["__id"]!;
        var secondId = saved[1].Values["__id"]!;
        var secretsBefore = new Dictionary<string, string?>(host.Secrets, StringComparer.Ordinal);

        host.FailSecretOperationNumber = 2;
        await Assert.ThrowsAsync<IOException>(() =>
            sut.SetItemsAsync(
                "profiles",
                [
                    ProfileItem("First", "http://localhost:11434", apiKey: "first-rotated", id: firstId),
                    ProfileItem("Second", "http://localhost:11435", apiKey: "second-rotated", id: secondId),
                ]
            )
        );
        host.FailSecretOperationNumber = 0;

        // The committed first write was compensated.
        Assert.Equal(secretsBefore, host.Secrets);
        Assert.Equal("first-key", host.Secrets[$"api-key.{firstId}"]);
        Assert.Equal("second-key", host.Secrets[$"api-key.{secondId}"]);

        var afterFailure = await sut.GetItemsAsync("profiles");
        Assert.Equal(2, afterFailure.Count);
    }

    [Fact]
    public async Task SetItemsAsync_WhenProfilePersistFailsAfterKeyRotation_DoesNotStrandTheNewKeyOnTheOldUrl()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Server", "http://old.invalid", apiKey: "old-key")]
        );
        var profileId = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"]!;
        var profilesBefore = host.GetSetting<JsonElement>("additionalProfiles").GetRawText();

        // Move the endpoint and rotate the key together, failing the metadata write after the
        // secret landed: the new key must not be left pointing at the old URL.
        host.FailSettingKey = "additionalProfiles";
        await Assert.ThrowsAsync<IOException>(() =>
            sut.SetItemsAsync(
                "profiles",
                [ProfileItem("Server", "http://new.invalid", apiKey: "new-key", id: profileId)]
            )
        );
        host.FailSettingKey = null;

        Assert.Equal("old-key", host.Secrets[$"api-key.{profileId}"]);
        Assert.Equal(profilesBefore, host.GetSetting<JsonElement>("additionalProfiles").GetRawText());
        Assert.Equal("http://old.invalid", Assert.Single(await sut.GetItemsAsync("profiles")).Values["baseUrl"]);
    }

    [Fact]
    public async Task AdditionalProfileRole_IsStableAcrossRepeatedGettersAndCapabilityRefresh()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", llmModel: "m1")]
        );

        var firstLlmRole = Assert.Single(sut.AdditionalLlmProviders);
        var firstTranscriptionRole = Assert.Single(sut.AdditionalTranscriptionEngines);
        Assert.Same(firstLlmRole, firstTranscriptionRole);
        Assert.Same(firstLlmRole, Assert.Single(sut.AdditionalLlmProviders));

        var refreshCountBefore = host.CapabilitiesChangedCount;
        var unchangedItems = await sut.GetItemsAsync("profiles");
        var result = await sut.SetItemsAsync("profiles", unchangedItems);

        Assert.True(result.IsSuccess);
        Assert.True(host.CapabilitiesChangedCount > refreshCountBefore);
        Assert.Same(firstLlmRole, Assert.Single(sut.AdditionalLlmProviders));
        Assert.Same(
            firstTranscriptionRole,
            Assert.Single(sut.AdditionalTranscriptionEngines)
        );
    }

    [Fact]
    public async Task AdditionalProfileRole_ChangedOrRemovedProfileInvalidatesCacheEntry()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Original", "http://localhost:11434", llmModel: "m1")]
        );

        var originalRole = Assert.Single(sut.AdditionalLlmProviders);
        var profileId = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Changed", "http://localhost:11434", llmModel: "m1", id: profileId)]
        );
        var changedRole = Assert.Single(sut.AdditionalLlmProviders);
        Assert.NotSame(originalRole, changedRole);

        await sut.SetItemsAsync("profiles", []);
        Assert.Empty(sut.AdditionalLlmProviders);

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Changed", "http://localhost:11434", llmModel: "m1", id: profileId)]
        );
        Assert.NotSame(changedRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task GetItemsAsync_DoesNotEchoApiKey()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123")]);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));

        Assert.Null(item.Values["api-key"]);
        Assert.Equal("Local Ollama", item.Values["name"]);
        Assert.Equal("http://localhost:11434", item.Values["baseUrl"]);
    }

    [Fact]
    public async Task SetItemsAsync_NullApiKey_KeepsStoredKeyAcrossUnrelatedSave()
    {
        // Pre-fix host behavior never delivered this null sentinel: it submitted
        // "" for both untouched and cleared fields, which the plugin kept. The
        // plugin's null behavior itself already kept the key; this pins the now
        // reachable untouched-secret contract.
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Original", "http://localhost:11434", apiKey: "stored-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Renamed", "http://localhost:11434", apiKey: null, id: id)]
        );

        Assert.Equal("stored-key", host.Secrets[secretReference]);
        Assert.Single(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);
    }

    [Fact]
    public async Task SetItemsAsync_BlankApiKeyWithStoredKey_DeletesSecretAndDropsCatalog()
    {
        // Pre-fix, NullIfWhiteSpace mapped "" to the keep sentinel, so no delete
        // occurred and the credential-bound catalog survived unchanged.
        var requestedApiKeys = new List<string?>();
        var handler = new CapturingHandler((request, _) =>
        {
            var apiKey = request.Headers.Authorization?.Parameter;
            requestedApiKeys.Add(apiKey);
            var model = apiKey is null ? "public-model" : "secured-model";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"data":[{"id":"{{model}}"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "stored-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";
        Assert.Contains(
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels,
            model => model.Id == "secured-model"
        );

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "", id: id)]
        );

        Assert.DoesNotContain(secretReference, host.Secrets.Keys);
        Assert.Contains(secretReference, host.DeletedSecretKeys);
        Assert.Equal(["stored-key", null], requestedApiKeys);
        var models = Assert.Single(sut.AdditionalLlmProviders).SupportedModels;
        Assert.Contains(models, model => model.Id == "public-model");
        Assert.DoesNotContain(models, model => model.Id == "secured-model");
    }

    [Fact]
    public async Task SetItemsAsync_BlankApiKeyWithoutStoredKey_IsNoOp()
    {
        // Pre-fix behavior was also a no-op for this keyless case; this is the
        // compatibility guard that ensures the new clear signal does not emit a
        // needless delete or discard an unrelated catalog.
        var modelRequests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            modelRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"m1"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "", id: id)]
        );

        Assert.Empty(host.Secrets);
        Assert.Empty(host.StoredSecrets);
        Assert.Empty(host.DeletedSecretKeys);
        Assert.Equal(1, modelRequests);
        Assert.Contains(
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels,
            model => model.Id == "m1"
        );
    }

    [Fact]
    public async Task SetItemsAsync_NonBlankApiKey_ReplacesStoredKey()
    {
        // Pre-fix replacement already worked. This regression guard proves the
        // new null/blank split leaves the existing non-blank path intact.
        var handler = new CapturingHandler((request, _) =>
        {
            var model = request.Headers.Authorization?.Parameter == "new-key"
                ? "new-key-model"
                : "old-key-model";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"data":[{"id":"{{model}}"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        var host = new TestPluginHostServices();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "old-key")]
        );
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        var secretReference = $"api-key.{id}";

        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", apiKey: "new-key", id: id)]
        );

        Assert.Equal("new-key", host.Secrets[secretReference]);
        Assert.Equal(
            [(secretReference, "old-key"), (secretReference, "new-key")],
            host.StoredSecrets
        );
        Assert.Empty(host.DeletedSecretKeys);
        var models = Assert.Single(sut.AdditionalLlmProviders).SupportedModels;
        Assert.Contains(models, model => model.Id == "new-key-model");
        Assert.DoesNotContain(models, model => model.Id == "old-key-model");
    }

    [Fact]
    public async Task AdditionalProfiles_PersistAndReloadWithSecret()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P1", "http://localhost:11434", apiKey: "k", llmModel: "m1")]);

        // A fresh instance over the same host (same settings + secrets) reloads them.
        var reloaded = new OpenAiCompatiblePlugin(httpClient);
        await reloaded.ActivateAsync(host);

        var llm = Assert.Single(reloaded.AdditionalLlmProviders);
        Assert.Equal("P1", llm.ProviderName);
        Assert.True(llm.IsAvailable);
    }

    [Fact]
    public async Task SetItemsAsync_RejectsInvalidBaseUrl()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync("profiles", [ProfileItem("Bad", "not-a-url")]);

        Assert.False(result.IsSuccess);
        Assert.Empty(sut.AdditionalLlmProviders);
    }

    [Fact]
    public async Task SetItemsAsync_EndpointChange_RefetchesCatalog()
    {
        // /v1/models returns different models depending on the server port, so we can
        // tell whether the catalog was refetched after the base URL changed.
        var handler = new CapturingHandler((request, _) =>
        {
            var models = request.RequestUri!.Port == 11434
                ? """{"data":[{"id":"m1"},{"id":"m2"}]}"""
                : """{"data":[{"id":"x1"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(models, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");

        // Re-save the SAME profile (same __id) pointing at a different server.
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:9999", id: id)]);

        var models = sut.AdditionalLlmProviders[0].SupportedModels.Select(m => m.Id).ToList();
        Assert.Contains("x1", models);
        Assert.DoesNotContain("m1", models); // stale catalog must not survive the endpoint change
    }

    [Fact]
    public async Task SetItemsAsync_EndpointChange_NormalizesSelectionsAndInvalidatesRole()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            var models = request.RequestUri!.Port == 11434
                ? """{"data":[{"id":"old-model"}]}"""
                : """{"data":[{"id":"new-model"},{"id":"new-model-2"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(models, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:11434",
                    model: "old-model",
                    llmModel: "old-model"
                ),
            ]
        );
        var originalRole = Assert.Single(sut.AdditionalLlmProviders);
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];

        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:9999",
                    model: "old-model",
                    llmModel: "old-model",
                    id: id
                ),
            ]
        );

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("new-model", item.Values["selectedModel"]);
        Assert.Equal("new-model", item.Values["selectedLlmModel"]);
        Assert.NotSame(originalRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_UpdatesProfileCatalog()
    {
        var modelsJson = """{"data":[{"id":"m1"}]}""";
        // Responder reads the current modelsJson each call, so we can simulate the
        // server's model list changing after the profile was first saved.
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Reading the reassigned-below modelsJson is the point (see comment above):
            // each call returns the server's current model list.
            // ReSharper disable once AccessToModifiedClosure
            Content = new StringContent(modelsJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");
        Assert.DoesNotContain(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");

        // Server gains a model; the dropdown-open refresh path should pick it up.
        modelsJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        await sut.RefreshModelCatalogAsync();

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_BaseUrlChange_DiscardsStaleResponse()
    {
        var oldRequestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseOldRequest = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new AsyncHandler(async (request, _) =>
        {
            if (request.RequestUri?.Port == 11434)
            {
                oldRequestStarted.TrySetResult(true);
                await releaseOldRequest.Task;
                return ModelCatalogResponse("old-a-model");
            }

            Assert.Equal(9999, request.RequestUri?.Port);
            return ModelCatalogResponse("b-model");
        });
        var host = CachedDefaultHost();
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var staleRefresh = sut.RefreshModelCatalogAsync();
        await oldRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await sut.SetSettingValueAsync("baseUrl", "http://localhost:9999");
            await sut.RefreshModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(30));

            releaseOldRequest.TrySetResult(true);
            await staleRefresh.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(["b-model"], sut.FetchedModels.Select(model => model.Id));
            Assert.Equal("b-model", sut.SelectedTranscriptionModelId);
            Assert.Equal("b-model", sut.SelectedLlmModelId);
            Assert.Equal(
                ["b-model"],
                JsonSerializer.Deserialize<List<FetchedModel>>(
                    host.GetSetting<string>("fetchedModels")!
                )!.Select(model => model.Id)
            );
            Assert.Equal("b-model", host.GetSetting<string>("selectedModel"));
            Assert.Equal("b-model", host.GetSetting<string>("selectedLlmModel"));
        }
        finally
        {
            releaseOldRequest.TrySetResult(true);
        }
    }

    [Theory]
    [InlineData("baseUrl", "http://localhost:9999", "http://localhost:11434")]
    [InlineData("apiVersion", "2025-04-01-preview", "2025-03-01-preview")]
    [InlineData("batchEndpoint", "deployment-scoped", "standard")]
    public async Task ValidateAsync_EndpointAba_DiscardsStaleResponse(string key, string changed, string original)
    {
        var originalRequestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseOriginalRequest = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var aRequestCount = 0;
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- pinning the port IS the parameter's purpose: every request in this test must target endpoint A.
        var handler = new AsyncHandler(async (request, _) =>
        {
            Assert.Equal(11434, request.RequestUri?.Port);
            // ReSharper disable once AccessToModifiedClosure -- the counter distinguishes the deliberately concurrent original and fresh A requests.
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (Interlocked.Increment(ref aRequestCount) == 1)
            {
                originalRequestStarted.TrySetResult(true);
                await releaseOriginalRequest.Task;
                return ModelCatalogResponse("stale-a-model");
            }

            return ModelCatalogResponse("fresh-a-model");
        });
        var host = CachedDefaultHost();
        host.SetSetting("apiVersion", "2025-03-01-preview");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var staleValidation = sut.ValidateAsync();
        await originalRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await sut.SetSettingValueAsync(key, changed);
            await sut.SetSettingValueAsync(key, original);
            await sut.RefreshModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(30));

            releaseOriginalRequest.TrySetResult(true);
            var result = await staleValidation.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.NotNull(result);
            Assert.False(result.IsSuccess);
            Assert.Equal(["fresh-a-model"], sut.FetchedModels.Select(model => model.Id));
            Assert.Equal("fresh-a-model", sut.SelectedTranscriptionModelId);
            Assert.Equal("fresh-a-model", sut.SelectedLlmModelId);
            Assert.Equal(
                ["fresh-a-model"],
                JsonSerializer.Deserialize<List<FetchedModel>>(
                    host.GetSetting<string>("fetchedModels")!
                )!.Select(model => model.Id)
            );
            Assert.Equal("fresh-a-model", host.GetSetting<string>("selectedModel"));
            Assert.Equal("fresh-a-model", host.GetSetting<string>("selectedLlmModel"));
        }
        finally
        {
            releaseOriginalRequest.TrySetResult(true);
        }
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_ApiKeyChange_DiscardsStaleResponse()
    {
        var oldKeyRequestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseOldKeyRequest = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new AsyncHandler(async (request, _) =>
        {
            var apiKey = request.Headers.Authorization?.Parameter;
            if (apiKey == "old-key")
            {
                oldKeyRequestStarted.TrySetResult(true);
                await releaseOldKeyRequest.Task;
                return ModelCatalogResponse("old-key-model");
            }

            Assert.Equal("new-key", apiKey);
            return ModelCatalogResponse("new-key-model");
        });
        var host = CachedDefaultHost();
        host.Secrets["api-key"] = "old-key";
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var staleRefresh = sut.RefreshModelCatalogAsync();
        await oldKeyRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await sut.SetApiKeyAsync("new-key");
            await sut.RefreshModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(30));

            releaseOldKeyRequest.TrySetResult(true);
            await staleRefresh.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(["new-key-model"], sut.FetchedModels.Select(model => model.Id));
            Assert.Equal("new-key-model", sut.SelectedTranscriptionModelId);
            Assert.Equal("new-key-model", sut.SelectedLlmModelId);
            Assert.Equal(
                ["new-key-model"],
                JsonSerializer.Deserialize<List<FetchedModel>>(
                    host.GetSetting<string>("fetchedModels")!
                )!.Select(model => model.Id)
            );
            Assert.Equal("new-key-model", host.GetSetting<string>("selectedModel"));
            Assert.Equal("new-key-model", host.GetSetting<string>("selectedLlmModel"));
        }
        finally
        {
            releaseOldKeyRequest.TrySetResult(true);
        }
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_RemovedDefaultModel_NormalizesBothSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m2");
        host.SetSetting("selectedLlmModel", "m2");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(
                new List<FetchedModel> { new("m1", null), new("m2", null) }
            )
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"m1"}]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync();

        Assert.Equal("m1", sut.SelectedTranscriptionModelId);
        Assert.Equal("m1", sut.SelectedLlmModelId);
        Assert.Equal("m1", host.GetSetting<string>("selectedModel"));
        Assert.Equal("m1", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_RemovedProfileModel_NormalizesBothSelections()
    {
        var modelsJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // ReSharper disable once AccessToModifiedClosure -- modelsJson is reassigned below before the refresh call, to simulate the server dropping a model.
            Content = new StringContent(modelsJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", model: "m2", llmModel: "m2")]
        );
        var originalRole = Assert.Single(sut.AdditionalLlmProviders);

        modelsJson = """{"data":[{"id":"m1"}]}""";
        await sut.RefreshModelCatalogAsync();

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("m1", item.Values["selectedModel"]);
        Assert.Equal("m1", item.Values["selectedLlmModel"]);
        Assert.NotSame(originalRole, Assert.Single(sut.AdditionalLlmProviders));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_SuccessfulEmptyCatalog_ClearsBothSelections()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m1");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("m1", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync();

        Assert.Empty(sut.FetchedModels);
        Assert.Null(sut.SelectedTranscriptionModelId);
        Assert.Null(sut.SelectedLlmModelId);
        Assert.Null(host.GetSetting<string>("selectedModel"));
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_TransientFailure_LeavesStateUntouched()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "default-m2");
        host.SetSetting("selectedLlmModel", "default-m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(
                new List<FetchedModel>
                {
                    new("default-m1", null),
                    new("default-m2", null),
                }
            )
        );
        var failModelRequests = false;
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting every request targets /v1/models is the intended verification.
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.EndsWith("/v1/models", request.RequestUri!.AbsolutePath);
            // ReSharper disable once AccessToModifiedClosure -- failModelRequests is flipped below, after the initial SetItemsAsync call, to simulate a transient failure on the subsequent refresh.
            if (failModelRequests)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"profile-m1"},{"id":"profile-m2"}]}""",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [
                ProfileItem(
                    "P",
                    "http://localhost:9999",
                    model: "profile-m2",
                    llmModel: "profile-m1"
                ),
            ]
        );

        failModelRequests = true;
        await sut.RefreshModelCatalogAsync();

        Assert.Equal(["default-m1", "default-m2"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("default-m2", sut.SelectedTranscriptionModelId);
        Assert.Equal("default-m1", sut.SelectedLlmModelId);
        Assert.Equal("default-m2", host.GetSetting<string>("selectedModel"));
        Assert.Equal("default-m1", host.GetSetting<string>("selectedLlmModel"));

        var profile = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("profile-m2", profile.Values["selectedModel"]);
        Assert.Equal("profile-m1", profile.Values["selectedLlmModel"]);
        Assert.Equal(
            ["profile-m1", "profile-m2"],
            Assert.Single(sut.AdditionalLlmProviders).SupportedModels.Select(m => m.Id)
        );
    }

    [Fact]
    public async Task ValidateAsync_TransientCatalogFailure_LeavesPriorStateUntouched()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m1");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("m1", null) })
        );
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"not-data":[]}""",
                Encoding.UTF8,
                "application/json"
            ),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(["m1"], sut.FetchedModels.Select(m => m.Id));
        Assert.Equal("m1", sut.SelectedTranscriptionModelId);
        Assert.Equal("m1", sut.SelectedLlmModelId);
        Assert.Equal("m1", host.GetSetting<string>("selectedModel"));
        Assert.Equal("m1", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ValidateAsync_InternalTaskCancellation_ReturnsFailureAndRetainsDefaultCache()
    {
        var host = CachedDefaultHost();
        using var httpClient = new HttpClient(
            new CapturingHandler((_, _) => throw new TaskCanceledException("client timeout"))
        );
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(["m1"], sut.FetchedModels.Select(model => model.Id));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_ProfileInternalCancellation_RetainsProfileCache()
    {
        var host = CachedProfileHost();
        using var httpClient = new HttpClient(
            new CapturingHandler((_, _) => throw new TaskCanceledException("client timeout"))
        );
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync(CancellationToken.None);

        var profile = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("m1", profile.Values["selectedModel"]);
        Assert.Equal(
            ["m1"],
            Assert.Single(sut.AdditionalTranscriptionEngines)
                .TranscriptionModels.Select(model => model.Id)
        );
    }

    [Fact]
    public async Task FetchModelsAsync_CallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var httpClient = new HttpClient(
            // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the
            // awaited call below, which completes before the using-scope disposes cts.
            new CapturingHandler((_, _) => throw new OperationCanceledException(cts.Token))
        );
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.FetchModelsAsync(cts.Token));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_ProfileCallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var host = CachedProfileHost();
        using var httpClient = new HttpClient(
            // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the
            // awaited call below, which completes before the using-scope disposes cts.
            new CapturingHandler((_, _) => throw new OperationCanceledException(cts.Token))
        );
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RefreshModelCatalogAsync(cts.Token));
    }

    [Fact]
    public async Task ValidateAsync_HttpClientPrivateTimeout_ReturnsFailureAndRetainsDefaultCache()
    {
        var host = CachedDefaultHost();
        using var httpClient = new HttpClient(
            new AsyncHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new UnreachableException();
            })
        );
        httpClient.Timeout = TimeSpan.FromMilliseconds(20);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(["m1"], sut.FetchedModels.Select(model => model.Id));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_ProfileHttpClientTimeout_RetainsCache()
    {
        var host = CachedProfileHost();
        using var httpClient = new HttpClient(
            new AsyncHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new UnreachableException();
            })
        );
        httpClient.Timeout = TimeSpan.FromMilliseconds(20);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync(CancellationToken.None);

        var profile = Assert.Single(await sut.GetItemsAsync("profiles"));
        Assert.Equal("m1", profile.Values["selectedModel"]);
    }

    [Fact]
    public async Task FetchModelsAsync_InternalCancellationRacingCallerCancellation_CallerWins()
    {
        using var cts = new CancellationTokenSource();
        using var httpClient = new HttpClient(
            new CapturingHandler((_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the
                // awaited call below, which completes before the using-scope disposes cts.
                cts.Cancel();
                throw new TaskCanceledException("both canceled");
            })
        );
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.FetchModelsAsync(cts.Token));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_ProfileInternalAndCallerCancellation_CallerWins()
    {
        using var cts = new CancellationTokenSource();
        var host = CachedProfileHost();
        using var httpClient = new HttpClient(
            new CapturingHandler((_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the
                // awaited call below, which completes before the using-scope disposes cts.
                cts.Cancel();
                throw new TaskCanceledException("both canceled");
            })
        );
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.RefreshModelCatalogAsync(cts.Token));
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThroughProfile_StreamsDeltas()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "",
            "");
        var handler = new CapturingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"m1"}]}""", Encoding.UTF8, "application/json"),
                };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices()); // streamResponses defaults true
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", llmModel: "m1")]);

        var role = Assert.Single(sut.AdditionalLlmProviders);
        var chunks = new List<string>();
        await foreach (var chunk in role.ProcessStreamingAsync("sys", "user", "m1", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
    }

    [Fact]
    public async Task ActivateAsync_PersistedProfilesContainNulls_SkipsThemAndKeepsValidOnes()
    {
        // Hand-edited or partially-written settings can carry nulls the declared types forbid.
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            JsonSerializer.Deserialize<JsonElement>(
                """
                [
                  null,
                  {"id":"profile-a","name":"A","baseUrl":null,"fetchedModels":null},
                  {"id":"profile-b","name":"B","baseUrl":"http://localhost:11434",
                   "fetchedModels":[null,{"id":"m1","ownedBy":null},{"id":"  "}]}
                ]
                """));
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var roles = sut.AdditionalLlmProviders;
        Assert.Equal(2, roles.Count);
        Assert.Equal(["A", "B"], roles.Select(r => r.ProviderName));
        // Only the null base URL was unusable, so only that profile is unconfigured.
        Assert.Equal([false, true], roles.Select(r => r.IsAvailable || r.SupportedModels.Count > 0));
        Assert.Equal(["m1"], roles[1].SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ProcessStreamingAsync_TokenCancelledMidStream_StopsConsumingResponse()
    {
        // The token reaches the enumerator as a plain parameter rather than through
        // WithCancellation, so pin that it still interrupts an unfinished stream.
        using var cts = new CancellationTokenSource();
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(
                new StalledSseStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n")),
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var consume = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", cts.Token))
            {
                chunks.Add(chunk);
                await cts.CancelAsync();
            }
        });

        // Bounded independently of the token under test, so a propagation regression fails here
        // instead of hanging the run.
        // ReSharper disable once MethodSupportsCancellation -- the cancellation-aware overload takes the token under test, the one dependency this bound must not have.
        await consume.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["Hel"], chunks);
    }

    /// <summary>Serves one SSE frame, then stalls like a server still generating tokens.</summary>
    private sealed class StalledSseStream(string firstFrame) : Stream
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly byte[] _frame = Encoding.UTF8.GetBytes(firstFrame);
        private int _offset;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _frame.Length)
            {
                var count = Math.Min(buffer.Length, _frame.Length - _offset);
                _frame.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return count;
            }

            Stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => responder(request, cancellationToken);
    }

    private static TestPluginHostServices CachedDefaultHost()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedModel", "m1");
        host.SetSetting("selectedLlmModel", "m1");
        host.SetSetting(
            "fetchedModels",
            JsonSerializer.Serialize(new List<FetchedModel> { new("m1", null) })
        );
        return host;
    }

    private static TestPluginHostServices CachedProfileHost()
    {
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            new List<OpenAiCompatibleProfile>
            {
                new()
                {
                    Id = "openai-compatible-profile",
                    Name = "Profile",
                    BaseUrl = "http://localhost:9999",
                    SelectedModelId = "m1",
                    SelectedLlmModelId = "m1",
                    FetchedModels = [new FetchedModel("m1", null)],
                },
            }
        );
        return host;
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public List<(string Key, string Value)> StoredSecrets { get; } = [];
        public List<string> DeletedSecretKeys { get; } = [];
        public List<(string Operation, string Key)> SecretOperations { get; } = [];
        public List<string> SettingWrites { get; } = [];
        public bool FailStoreSecretWrites { get; set; }
        public int StoreSecretFailAfter { get; set; } = int.MaxValue;
        public bool FailDeleteSecretWrites { get; set; }
        public int CapabilitiesChangedCount { get; private set; }

        /// <summary>
        ///     When set, the Nth (1-based) secret write or delete from that point on throws, so a
        ///     caller's mid-sequence failure handling can be exercised. Assigning resets the count.
        /// </summary>
        public int FailSecretOperationNumber
        {
            get;
            set
            {
                field = value;
                _secretOperations = 0;
            }
        }

        private int _secretOperations;

        public Task StoreSecretAsync(string key, string value)
        {
            // Two independent injection seams: the counter-based one fails the Nth operation,
            // the flags fail every write. Count the attempt first so the counter stays accurate.
            ThrowIfInjectedFailure();
            if (FailStoreSecretWrites || StoredSecrets.Count >= StoreSecretFailAfter)
                throw new IOException("Simulated secret-store failure.");

            StoredSecrets.Add((key, value));
            SecretOperations.Add(("store", key));
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        private void ThrowIfInjectedFailure()
        {
            if (++_secretOperations == FailSecretOperationNumber)
                throw new IOException("Simulated secret-store failure.");
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            ThrowIfInjectedFailure();
            if (FailDeleteSecretWrites)
                throw new IOException("Simulated secret-delete failure.");

            DeletedSecretKeys.Add(key);
            SecretOperations.Add(("delete", key));
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     When set, writing this settings key throws, so a caller's handling of a metadata
        ///     write that fails after its secret writes succeeded can be exercised.
        /// </summary>
        public string? FailSettingKey { get; set; }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value)
        {
            // Throw before recording: a write that failed must not appear to have happened.
            if (key == FailSettingKey)
                throw new IOException("Simulated settings-store failure.");

            SettingWrites.Add(key);
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
            CapabilitiesChangedCount++;
        }
        public IPluginLocalization Localization { get; set; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
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
