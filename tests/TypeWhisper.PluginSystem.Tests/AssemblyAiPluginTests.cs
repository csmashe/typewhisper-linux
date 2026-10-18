using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.AssemblyAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

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
        Assert.Equal("""["universal-3-pro"]""", root.GetProperty("speech_models").GetRawText());
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
        Assert.Equal("""["universal-3-pro"]""", body.RootElement.GetProperty("speech_models").GetRawText());
        Assert.Equal(300, body.RootElement.GetProperty("keyterms_prompt").GetArrayLength());
    }

    [Fact]
    public void CreateSubmitBody_OmitsKeytermsWhenThereAreNone()
    {
        var body = AssemblyAiPlugin.CreateSubmitBody("https://cdn.example/audio", "universal-2", null, []);

        Assert.True((bool)body["language_detection"]);
        Assert.False(body.ContainsKey("keyterms_prompt"));
    }

    [Fact]
    public void KeytermsFor_IgnoresPromptTextAndLegacyCommaPrompts()
    {
        Assert.Empty(AssemblyAiPlugin.KeytermsFor("Acme, Beta", true, AssemblyAiPlugin.BudgetFor("universal-3-pro")));
        Assert.Equal(
            ["Acme"],
            AssemblyAiPlugin.KeytermsFor(
                PluginTranscriptionPrompt.Encode("Beta, Gamma", ["Acme"]),
                true,
                AssemblyAiPlugin.BudgetFor("universal-3-pro")));
    }

    [Fact]
    public void KeytermsFor_DropsPhrasesOverSixWordsAndCapsTotalWordsPerModel()
    {
        var terms = Enumerable.Range(0, 600).Select(i => $"alpha{i} beta{i}").Prepend("one two three four five six seven");
        var prompt = PluginTranscriptionPrompt.Encode(null, terms);

        var pro = AssemblyAiPlugin.KeytermsFor(prompt, true, AssemblyAiPlugin.BudgetFor("universal-3-pro"));
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
        var options = await new AssemblyAiWebSocketAdapter("key", "de", keyterms)
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
