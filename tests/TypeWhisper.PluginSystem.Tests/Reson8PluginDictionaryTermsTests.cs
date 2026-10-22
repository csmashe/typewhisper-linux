using System.Web;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// See Reson8PluginTests.cs: the PCM byte arrays are audio, and asserting on the
// captured request inside the handler is what these tests verify.
// ReSharper disable UseUtf8StringLiteral
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    [Fact]
    public void Role_OptsIntoStructuredTermsAndLanguageHintsWithReson8Limits()
    {
        var sut = new Reson8Plugin();

        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.True(sut.SupportsLanguageHints);
        Assert.Equal(new DictionaryTermsBudget(MaxTerms: 250, MaxCharsPerTerm: 255), sut.DictionaryTermsBudget);
    }

    [Fact]
    public async Task TranscribeAsync_SendsDictionaryTermsAsPhrasesWithoutCommaTermsOrPromptText()
    {
        string? query = null;
        var sut = await CreateBatchPluginAsync(request => query = request.RequestUri!.Query);

        await sut.TranscribeAsync(
            BuildPcm16Wav([0x00, 0x00]),
            "de",
            false,
            PluginTranscriptionPrompt.Encode("Caller context", ["TypeWhisper", "Washington, D.C.", "Smörgåsbord & Co"]),
            CancellationToken.None);

        var parameters = HttpUtility.ParseQueryString(query!);
        Assert.Equal("TypeWhisper,Smörgåsbord & Co", parameters["phrases"]);
        Assert.Equal("de", parameters["language"]);
        Assert.DoesNotContain("Caller", query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("plain caller prompt")]
    public async Task TranscribeAsync_OmitsPhrasesWithoutDictionaryTerms(string? prompt)
    {
        string? query = null;
        var sut = await CreateBatchPluginAsync(request => query = request.RequestUri!.Query);

        await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, prompt, CancellationToken.None);

        Assert.DoesNotContain("phrases=", query);
    }

    [Fact]
    public async Task TranscribeAsync_SendsNoPhrasesWithACustomModel()
    {
        string? query = null;
        var sut = await CreateBatchPluginAsync(request => query = request.RequestUri!.Query, "domain-model");

        await sut.TranscribeAsync(
            BuildPcm16Wav([0x00, 0x00]),
            null,
            false,
            PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]),
            CancellationToken.None);

        Assert.Contains("custom_model_id=domain-model", query);
        Assert.DoesNotContain("phrases=", query);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsAMalformedEnvelopeWithoutSending()
    {
        var sent = false;
        var sut = await CreateBatchPluginAsync(_ => sent = true);

        await Assert.ThrowsAsync<FormatException>(() => sut.TranscribeAsync(
            BuildPcm16Wav([0x00, 0x00]),
            null,
            false,
            PluginTranscriptionPrompt.EnvelopePrefix + "{",
            CancellationToken.None));
        Assert.False(sent);
    }

    [Fact]
    public void PhrasesFor_KeepsTheEncodedQueryWithinItsLimitInOrder()
    {
        var terms = Enumerable.Range(0, 250).Select(i => $"Fachbegriff Nummer {i:D3} äöü").ToList();

        var phrases = Reson8Plugin.PhrasesFor(PluginTranscriptionPrompt.Encode(null, terms), Reson8Plugin.DefaultModelId);

        Assert.NotEmpty(phrases);
        Assert.Equal(terms.Take(phrases.Count), phrases);
        Assert.True(Reson8Plugin.EncodePhrases(phrases).Length <= Reson8Plugin.MaxPhrasesQueryLength);
        Assert.True(Reson8Plugin.EncodePhrases([.. phrases, terms[phrases.Count]]).Length > Reson8Plugin.MaxPhrasesQueryLength);
    }

    [Fact]
    public void PhrasesFor_AppliesTheTermAndLengthLimits()
    {
        var terms = Enumerable.Range(0, 300).Select(i => $"t{i}").Append(new string('x', 256)).ToList();

        var phrases = Reson8Plugin.PhrasesFor(PluginTranscriptionPrompt.Encode(null, terms), Reson8Plugin.DefaultModelId);

        Assert.Equal(terms.Take(250), phrases);
    }

    [Theory]
    [InlineData(new[] { "de", "en" }, "de,en")]
    [InlineData(new[] { "de", "auto", " en ", "DE" }, "de,en")]
    [InlineData(new[] { "de" }, null)]
    [InlineData(new string[0], null)]
    public async Task TranscribeWithLanguageHintsAsync_ConstrainsDetectionToSeveralHintsOnly(
        string[] hints,
        string? expectedLanguage)
    {
        string? query = null;
        var sut = await CreateBatchPluginAsync(request => query = request.RequestUri!.Query, response: """{ "text": "Hallo" }""");

        var result = await sut.TranscribeWithLanguageHintsAsync(
            BuildPcm16Wav([0x00, 0x00]),
            hints,
            false,
            null,
            CancellationToken.None);

        Assert.Equal(expectedLanguage, HttpUtility.ParseQueryString(query!)["language"]);
        // A candidate list says nothing about which language was spoken.
        Assert.Null(result.DetectedLanguage);
    }

    [Fact]
    public async Task StartStreamingWithLanguageHintsAndPromptAsync_PinsOneHintListsSeveralAndCarriesPhrases()
    {
        var requests = new List<Reson8StreamingRequest>();
        var sut = await CreateStreamingPluginAsync(
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<IStreamingSession>(new FakeStreamingSession());
            },
            () => { });
        var prompt = PluginTranscriptionPrompt.Encode(null, ["TypeWhisper", "Washington, D.C."]);

        await using (await sut.StartStreamingWithLanguageHintsAndPromptAsync(["de"], prompt, CancellationToken.None)) { }
        await using (await sut.StartStreamingWithLanguageHintsAndPromptAsync(["de", "nl"], prompt, CancellationToken.None)) { }
        await using (await sut.StartStreamingWithLanguageHintsAsync(["de"], CancellationToken.None)) { }

        Assert.Equal("de", requests[0].Language);
        Assert.Equal(["TypeWhisper"], requests[0].Phrases);
        // The session connects with the model the phrases were chosen for.
        Assert.Equal(Reson8Plugin.DefaultModelId, requests[0].ModelId);
        Assert.Equal("de,nl", requests[1].Language);
        Assert.Null(requests[2].Language);
        Assert.Empty(requests[2].Phrases);
    }

    [Fact]
    public async Task TranscribeStreamingWithLanguageHintsAsync_ReportsTheDetectedLanguageAndFallsBackWithTheSameRequest()
    {
        Reson8StreamingRequest? streamed = null;
        var session = new FakeStreamingSession
        {
            OnFinalize = (fake, _) =>
            {
                fake.Raise(new StreamingTranscriptEvent("Hallo wereld", true) { DetectedLanguage = "nl" });
                return Task.CompletedTask;
            },
        };
        var sut = await CreateStreamingPluginAsync(
            (request, _) =>
            {
                streamed = request;
                return Task.FromResult<IStreamingSession>(session);
            },
            () => { });

        var result = await sut.TranscribeStreamingWithLanguageHintsAsync(
            BuildPcm16Wav([0x00, 0x00]),
            ["de", "nl"],
            false,
            PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]),
            _ => true,
            CancellationToken.None);

        Assert.Equal("de,nl", streamed!.Language);
        Assert.Equal(["TypeWhisper"], streamed.Phrases);
        Assert.Equal(Reson8Plugin.DefaultModelId, streamed.ModelId);
        Assert.Equal("nl", result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_BatchFallbackKeepsLanguageListAndPhrases()
    {
        string? batchQuery = null;
        var handler = new CapturingHandler((request, _) =>
        {
            batchQuery = request.RequestUri!.Query;
            return JsonResponse("""{ "text": "batch" }""");
        });
        var sut = new Reson8Plugin(new HttpClient(handler))
        {
            StreamingConnectTimeout = TimeSpan.FromMilliseconds(200),
            StreamingFinalizeGrace = TimeSpan.FromMilliseconds(200),
            StreamingSessionFactory = (_, _) => throw new InvalidOperationException("connect failed"),
        };
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } });

        var result = await sut.TranscribeStreamingWithLanguageHintsAsync(
            BuildPcm16Wav([0x00, 0x00]),
            ["de", "nl"],
            false,
            PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]),
            _ => true,
            CancellationToken.None);

        Assert.Equal("batch", result.Text);
        var parameters = HttpUtility.ParseQueryString(batchQuery!);
        Assert.Equal("de,nl", parameters["language"]);
        Assert.Equal("TypeWhisper", parameters["phrases"]);
    }

    [Fact]
    public void RealtimeUri_CarriesPhrasesAndALanguageList()
    {
        var uri = Reson8StreamingSession.BuildRealtimeUri(
            "https://api.reson8.dev",
            null,
            "de,en",
            ["TypeWhisper", "Smörgåsbord & Co"]);

        var parameters = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("de,en", parameters["language"]);
        Assert.Equal("TypeWhisper,Smörgåsbord & Co", parameters["phrases"]);

        Assert.DoesNotContain(
            "phrases=",
            Reson8StreamingSession.BuildRealtimeUri("https://api.reson8.dev", null, null, []).Query);
    }

    private static async Task<Reson8Plugin> CreateBatchPluginAsync(
        Action<HttpRequestMessage> onRequest,
        string? selectedModel = null,
        string response = """{ "text": "Hallo", "language": "de" }""")
    {
        var handler = new CapturingHandler((request, _) =>
        {
            onRequest(request);
            return JsonResponse(response);
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        if (selectedModel is not null)
        {
            host.SetSetting("selectedModel", selectedModel);
            host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel(selectedModel, "Domain Model", null, null) });
        }

        var sut = new Reson8Plugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }
}
