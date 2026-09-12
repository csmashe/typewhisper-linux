using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services.Plugins;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.MicrosoftAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class MicrosoftAiPluginTests
{
    [Fact]
    public void ManifestAndPlugin_ExposeMicrosoftAiMetadataAndFallbackModels()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(PluginDirectory(), "manifest.json")));
        using var sut = new MicrosoftAiPlugin();

        Assert.Equal("com.typewhisper.microsoft-ai", sut.PluginId);
        Assert.Equal("microsoft-ai", sut.ProviderId);
        Assert.Equal("Microsoft AI (MAI Transcribe)", sut.ProviderDisplayName);
        Assert.Equal(sut.PluginId, manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal(sut.PluginVersion, manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal("transcription", manifest.RootElement.GetProperty("categories")[0].GetString());
        Assert.True(manifest.RootElement.GetProperty("requiresApiKey").GetBoolean());
        Assert.Equal(
            [MicrosoftAiPlugin.DefaultModelId, MicrosoftAiPlugin.LegacyModelId],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal([60, 43], sut.TranscriptionModels.Select(model => model.LanguageCount).ToArray());
        Assert.False(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
        Assert.False(sut.SupportsLanguageHints);
        Assert.Equal(60, sut.SupportedLanguages.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("yue", sut.SupportedLanguages);
    }

    [Fact]
    public void SupportedLanguages_SwitchesWithSelectedModelAndMatchesCatalogCounts()
    {
        using var sut = new MicrosoftAiPlugin();
        var currentLanguages = sut.SupportedLanguages.ToArray();
        string[] legacyLanguages =
        [
            "ar", "as", "bg", "bn", "ca", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "gu",
            "hi", "hu", "id", "it", "ja", "kn", "ko", "lt", "ml", "mr", "nb", "nl", "or", "pa", "pl",
            "pt", "ro", "ru", "sk", "sl", "sv", "ta", "te", "th", "tr", "uk", "vi", "zh",
        ];

        sut.SelectModel(MicrosoftAiPlugin.LegacyModelId);
        Assert.Equal(legacyLanguages, sut.SupportedLanguages);
        Assert.Equal(43, sut.SupportedLanguages.Count);
        Assert.Equal(sut.SupportedLanguages.Count,
            sut.TranscriptionModels.Single(model => model.Id == sut.SelectedModelId).LanguageCount);
        Assert.Equal(
            ["af", "az", "bs", "fa", "fil", "gl", "he", "hy", "is", "kk", "lv", "mk", "ms", "ne", "sw", "ur", "yue"],
            currentLanguages.Except(sut.SupportedLanguages).ToArray());

        sut.SelectModel(MicrosoftAiPlugin.DefaultModelId);
        Assert.Equal(currentLanguages, sut.SupportedLanguages);
        Assert.Equal(60, sut.SupportedLanguages.Count);
        Assert.Equal(sut.SupportedLanguages.Count,
            sut.TranscriptionModels.Single(model => model.Id == sut.SelectedModelId).LanguageCount);
    }

    [Theory]
    [InlineData("typewhisper-speech", "https://typewhisper-speech.cognitiveservices.azure.com/")]
    [InlineData("https://eastus.api.cognitive.microsoft.com/", "https://eastus.api.cognitive.microsoft.com/")]
    [InlineData("https://example.services.ai.azure.com", "https://example.services.ai.azure.com/")]
    [InlineData("https://example.openai.azure.us", "https://example.openai.azure.us/")]
    public void NormalizeEndpoint_AcceptsResourceNamesAndAzureHosts(string input, string expected)
    {
        Assert.Equal(expected, MicrosoftAiPlugin.NormalizeEndpoint(input)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.cognitiveservices.azure.com.evil.test")]
    [InlineData("https://example.cognitiveservices.azurе.com")]
    [InlineData("https://cognitiveservices.azure.com")]
    [InlineData("https://example.cognitiveservices.azure.com#fragment")]
    [InlineData("-resource")]
    [InlineData("resource-")]
    [InlineData("")]
    [InlineData("not a resource")]
    [InlineData("http://example.cognitiveservices.azure.com")]
    [InlineData("https://example.com")]
    [InlineData("https://example.cognitiveservices.azure.com/path")]
    [InlineData("https://user@example.cognitiveservices.azure.com")]
    [InlineData("https://example.cognitiveservices.azure.com?key=secret")]
    public void NormalizeEndpoint_RejectsUnsafeOrMalformedValues(string input)
    {
        Assert.Null(MicrosoftAiPlugin.NormalizeEndpoint(input));
    }

    [Fact]
    public void RegionalEndpointAvailability_IdentifiesUnsupportedRegions()
    {
        var westEurope = MicrosoftAiPlugin.NormalizeEndpoint(
            "https://westeurope.api.cognitive.microsoft.com")!;
        var northEurope = MicrosoftAiPlugin.NormalizeEndpoint(
            "https://northeurope.api.cognitive.microsoft.com")!;
        var resource = MicrosoftAiPlugin.NormalizeEndpoint("speech-demo")!;

        Assert.Equal("westeurope", MicrosoftAiPlugin.GetRegionalEndpointRegion(westEurope));
        Assert.Equal("westeurope", MicrosoftAiPlugin.GetUnsupportedMaiRegion(westEurope));
        Assert.Null(MicrosoftAiPlugin.GetUnsupportedMaiRegion(northEurope));
        Assert.Null(MicrosoftAiPlugin.GetRegionalEndpointRegion(resource));
    }

    [Fact]
    public async Task ConnectionAndSettings_ArePersistedAndReloaded()
    {
        var host = new TestPluginHostServices();
        using var sut = new MicrosoftAiPlugin();
        await sut.ActivateAsync(host);

        sut.SetEndpoint("speech-demo");
        await sut.SetApiKeyAsync(" azure-key ");
        sut.SetTranscriptStyle(MicrosoftAiTranscriptStyle.Verbatim);
        sut.SetSpeakerDiarizationEnabled(true);
        sut.SelectModel(MicrosoftAiPlugin.LegacyModelId);

        Assert.True(sut.IsConfigured);
        Assert.Equal("azure-key", host.Secrets["api-key"]);
        Assert.Equal(
            "https://speech-demo.cognitiveservices.azure.com",
            host.GetSetting<string>("endpoint"));
        Assert.Equal("verbatim", host.GetSetting<string>("transcriptStyle"));
        Assert.False(host.GetSetting<bool>("speakerDiarizationEnabled"));
        Assert.Equal(MicrosoftAiPlugin.LegacyModelId, host.GetSetting<string>("selectedModel"));

        using var reloaded = new MicrosoftAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.True(reloaded.IsConfigured);
        Assert.Equal(MicrosoftAiPlugin.LegacyModelId, reloaded.SelectedModelId);
        Assert.Equal(MicrosoftAiTranscriptStyle.Verbatim, reloaded.TranscriptStyle);
        Assert.False(reloaded.SelectedModelSupportsDiarization);

        reloaded.SetEndpoint("");
        await reloaded.SetApiKeyAsync("");
        Assert.False(reloaded.IsConfigured);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task TranscribeAsync_BuildsMultipartRequestAndParsesSpeakersAndTimings()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://speech-demo.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
                request.RequestUri?.AbsoluteUri);
            Assert.True(request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var keys));
            Assert.Equal("azure-key", Assert.Single(keys));
            Assert.False(request.Headers.Contains("Authorization"));
            Assert.Equal("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);

            Assert.NotNull(body);
            Assert.Contains("name=definition", body);
            Assert.Contains("name=audio", body);
            Assert.Contains("filename=audio.wav", body);
            using var definition = ReadDefinition(request);
            var root = definition.RootElement;
            AssertCurrentEnhancedMode(root, "verbatim");
            Assert.Equal("de-DE", Assert.Single(root.GetProperty("locales").EnumerateArray()).GetString());
            Assert.True(root.GetProperty("diarization").GetProperty("enabled").GetBoolean());
            Assert.Equal(["TypeWhisper", "Azure"], root.GetProperty("phraseList").GetProperty("phrases")
                .EnumerateArray().Select(phrase => phrase.GetString()!).ToArray());
            Assert.Equal("None", root.GetProperty("profanityFilterMode").GetString());
            return JsonResponse(SuccessResponse);
        });
        var host = ConfiguredHost();
        host.SetSetting("transcriptStyle", "verbatim");
        host.SetSetting("speakerDiarizationEnabled", true);
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de_DE",
            translate: false,
            "TypeWhisper, Azure",
            CancellationToken.None);

        Assert.Equal("Speaker 1: Hallo Welt\nSpeaker 2: Willkommen", result.Text);
        Assert.Equal("de-DE", result.DetectedLanguage);
        Assert.Equal(2.75, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Speaker 1: Hallo Welt", result.Segments[0].Text);
        Assert.Equal(0.25, result.Segments[0].Start);
        Assert.Equal(1.75, result.Segments[0].End);
        Assert.Equal("Speaker 2: Willkommen", result.Segments[1].Text);
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("verbatim")]
    public async Task TranscribeAsync_UsesLegacyStyleContractAndOmitsUnsupportedDiarization(string style)
    {
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            using var definition = ReadDefinition(request);
            var root = definition.RootElement;
            var enhanced = root.GetProperty("enhancedMode");
            Assert.True(enhanced.GetProperty("enabled").GetBoolean());
            Assert.Equal(MicrosoftAiPlugin.LegacyModelId, enhanced.GetProperty("model").GetString());
            Assert.False(enhanced.TryGetProperty("modelOptions", out _));
            Assert.False(enhanced.TryGetProperty("timestamps", out _));
            if (style == "verbatim")
                Assert.Equal("verbatim", enhanced.GetProperty("transcribeStyle").GetString());
            else
                Assert.False(enhanced.TryGetProperty("transcribeStyle", out _));
            Assert.False(root.TryGetProperty("transcribeStyle", out _));
            Assert.False(root.TryGetProperty("diarization", out _));
            Assert.False(root.TryGetProperty("locales", out _));
            Assert.False(root.TryGetProperty("phraseList", out _));
            Assert.Equal("None", root.GetProperty("profanityFilterMode").GetString());
            return JsonResponse(SuccessResponse);
        })));
        var host = ConfiguredHost();
        host.SetSetting("selectedModel", MicrosoftAiPlugin.LegacyModelId);
        host.SetSetting("transcriptStyle", style);
        host.SetSetting("speakerDiarizationEnabled", true);
        await sut.ActivateAsync(host);

        await sut.TranscribeAsync([1], null, false, null, CancellationToken.None);
    }

    [Theory]
    [InlineData(MicrosoftAiPlugin.LegacyModelId, "yue", null)]
    [InlineData(MicrosoftAiPlugin.DefaultModelId, "yue", "yue")]
    [InlineData(MicrosoftAiPlugin.LegacyModelId, "YUE_HK", null)]
    [InlineData(MicrosoftAiPlugin.LegacyModelId, "de_DE", "de-DE")]
    [InlineData(MicrosoftAiPlugin.LegacyModelId, "EN", "EN")]
    [InlineData(MicrosoftAiPlugin.LegacyModelId, "auto", null)]
    public async Task TranscribeAsync_UsesOnlySupportedLegacyLocalesAndTracesFallback(
        string modelId, string language, string? expectedLocale)
    {
        var handler = new CapturingHandler((request, body) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var definition = form.Single(part =>
                part.Headers.ContentDisposition?.Name?.Trim('"') == "definition");
            using var json = JsonDocument.Parse(definition.ReadAsStringAsync().GetAwaiter().GetResult());
            if (expectedLocale is null)
                Assert.False(json.RootElement.TryGetProperty("locales", out _));
            else
                Assert.Equal(expectedLocale, Assert.Single(json.RootElement.GetProperty("locales")
                    .EnumerateArray()).GetString());
            return JsonResponse(SuccessResponse);
        });
        var host = ConfiguredHost();
        using var sut = new MicrosoftAiPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        sut.SelectModel(modelId);

        await sut.TranscribeAsync([1], language, false, null, CancellationToken.None);

        var fallbackLogs = host.Logs.Where(log => log.Message.Contains("automatic detection")).ToArray();
        if (expectedLocale is null && language != "auto")
        {
            var log = Assert.Single(fallbackLogs);
            Assert.Equal(PluginLogLevel.Trace, log.Level);
            Assert.Contains(modelId, log.Message);
            Assert.Contains(language.Replace('_', '-'), log.Message);
        }
        else
            Assert.Empty(fallbackLogs);
    }

    [Fact]
    public async Task MultipleLanguageHints_UseFirstHintViaSdkDefault()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            using var definition = ReadDefinition(request);
            Assert.Equal("de", Assert.Single(definition.RootElement.GetProperty("locales").EnumerateArray()).GetString());
            return JsonResponse("""{"combinedPhrases":[{"text":"Hello"}],"phrases":[]}""");
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        await ((ITranscriptionEngineRole)sut).TranscribeWithLanguageHintsAsync(
            [1],
            ["de", "en"],
            false,
            null,
            CancellationToken.None);
    }

    [Fact]
    public async Task RefreshModelCatalog_FiltersPersistsAndMergesMaiModels()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(
                "https://speech-demo.cognitiveservices.azure.com/openai/v1/models",
                request.RequestUri?.AbsoluteUri);
            Assert.True(request.Headers.TryGetValues("api-key", out var keys));
            Assert.Equal("azure-key", Assert.Single(keys));
            return JsonResponse("""
                {"data":[
                  {"id":"gpt-5"},
                  {"id":"mai-transcribe-2-preview"},
                  {"id":"mai-transcribe-2"}
                ]}
                """);
        });
        var host = ConfiguredHost();
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        Assert.True(await sut.RefreshModelCatalogAsync());
        Assert.Equal(
            ["MAI-Transcribe-2", "mai-transcribe-2-preview", "MAI-Transcribe-1.5"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(
            ["mai-transcribe-2", "mai-transcribe-2-preview"],
            host.GetSetting<IReadOnlyList<string>>("cachedModels"));
    }

    [Fact]
    public async Task UnavailableModelCatalog_KeepsStaticFallbacks()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"Not found"}}"""),
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        Assert.False(await sut.RefreshModelCatalogAsync());
        Assert.Equal(
            [MicrosoftAiPlugin.DefaultModelId, MicrosoftAiPlugin.LegacyModelId],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.RequestTimeout, PluginRequestFailureKind.Timeout)]
    [InlineData(HttpStatusCode.Redirect, PluginRequestFailureKind.Unknown)]
    [InlineData(HttpStatusCode.Unauthorized, PluginRequestFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, PluginRequestFailureKind.Authentication)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, PluginRequestFailureKind.RateLimit)]
    [InlineData(HttpStatusCode.InternalServerError, PluginRequestFailureKind.ServerError)]
    public async Task HttpFailures_AreMappedWithoutExposingCredentials(
        HttpStatusCode statusCode,
        PluginRequestFailureKind expectedKind)
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""{"message":"request failed for azure-key"}"""),
        });
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(ConfiguredHost());

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            [1], null, false, null, CancellationToken.None));
        Assert.Equal(expectedKind, error.FailureKind);
        Assert.Equal((int)statusCode, error.HttpStatusCode);
        Assert.DoesNotContain("azure-key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpUnauthorized_UsesGermanCatalog()
    {
        var host = ConfiguredHost();
        host.Localization = new PluginLocalization(PluginDirectory(), "de");
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1], null, false, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Authentication, error.FailureKind);
        Assert.Equal("Azure Speech hat den API-Key abgelehnt. Prüfe den Schlüssel und den Endpunkt der ausgewählten Speech-Ressource.",
            error.Message);
    }

    [Fact]
    public async Task TranscriptionErrors_WithoutLocalizationFallBackToKeys()
    {
        using var sut = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1], null, false, null, CancellationToken.None));
        Assert.Equal("Error.ConfigurationMissing", error.Message);
        var invalid = Assert.Throws<PluginRequestException>(() => MicrosoftAiPlugin.ParseResponse("{}"));
        Assert.Equal("Error.InvalidResponse", invalid.Message);
    }

    [Fact]
    public async Task UnsupportedRegionalEndpoint_ReturnsActionableGuidance()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"code\":\"InvalidRequest\",\"message\":\"Enhanced mode with model is currently not supported yet.\"}"),
        });
        var host = ConfiguredHost("https://westeurope.api.cognitive.microsoft.com");
        using var client = new HttpClient(handler);
        using var sut = new MicrosoftAiPlugin(client);
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            [1], "de", false, null, CancellationToken.None));
        Assert.Equal(host.Localization.GetString("Settings.RegionUnsupported", "westeurope",
            "centralindia, eastus, northeurope, southeastasia, westus, westus2"), error.Message);
        Assert.DoesNotContain("azure-key", error.Message);
    }

    [Fact]
    public async Task ConfigurationTranslationAndDurationLimits_AreRejectedBeforeNetworking()
    {
        using var unconfigured = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await unconfigured.ActivateAsync(new TestPluginHostServices());
        var missing = await Assert.ThrowsAsync<PluginRequestException>(() => unconfigured.TranscribeAsync(
            [1], null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, missing.FailureKind);

        using var configured = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await configured.ActivateAsync(ConfiguredHost());
        var translation = await Assert.ThrowsAsync<PluginRequestException>(() => configured.TranscribeAsync(
            [1], null, true, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.InvalidRequest, translation.FailureKind);

        var duration = await Assert.ThrowsAsync<PluginRequestException>(() => configured.TranscribeAsync(
            LongDurationWav(), null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, duration.FailureKind);
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(PluginDirectory(), "manifest.json")));
        using var sut = new MicrosoftAiPlugin();
        Assert.Equal(manifest.RootElement.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public async Task Settings_RoundTripClampAndNotifyOnlyWhenConfigurednessChanges()
    {
        var host = new TestPluginHostServices();
        using var sut = new MicrosoftAiPlugin();
        await sut.ActivateAsync(host);
        Assert.Equal("false", await sut.GetSettingValueAsync("speakerDiarizationEnabled"));
        await sut.SetSettingValueAsync("endpoint", " speech-demo ");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        await sut.SetSettingValueAsync("api-key", " azure-key ");
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        await sut.SetSettingValueAsync("api-key", "replacement");
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        Assert.Equal("replacement", await sut.GetSettingValueAsync("api-key"));
        Assert.Equal("https://speech-demo.cognitiveservices.azure.com", await sut.GetSettingValueAsync("endpoint"));
        await sut.SetSettingValueAsync("transcriptStyle", "verbatim");
        await sut.SetSettingValueAsync("speakerDiarizationEnabled", "true");
        Assert.Equal("verbatim", await sut.GetSettingValueAsync("transcriptStyle"));
        Assert.Equal("true", await sut.GetSettingValueAsync("speakerDiarizationEnabled"));
        await sut.SetSettingValueAsync("selectedModel", MicrosoftAiPlugin.LegacyModelId);
        await sut.SetSettingValueAsync("speakerDiarizationEnabled", "true");
        Assert.Equal("false", await sut.GetSettingValueAsync("speakerDiarizationEnabled"));
        Assert.False(host.GetSetting<bool>("speakerDiarizationEnabled"));
        await sut.SetSettingValueAsync("selectedModel", "unknown");
        Assert.Equal(MicrosoftAiPlugin.LegacyModelId, await sut.GetSettingValueAsync("selectedModel"));
        Assert.Throws<ArgumentException>(() => sut.SelectModel("unknown"));
        await sut.SetSettingValueAsync("transcriptStyle", null);
        Assert.Equal("clean", await sut.GetSettingValueAsync("transcriptStyle"));
        await sut.SetSettingValueAsync("endpoint", " invalid endpoint ");
        Assert.Equal("invalid endpoint", await sut.GetSettingValueAsync("endpoint"));
        await sut.SetSettingValueAsync("api-key", null);
        Assert.Null(await sut.GetSettingValueAsync("api-key"));
        Assert.Empty(host.Secrets);
        Assert.Null(await sut.GetSettingValueAsync("unknown"));
    }

    [Theory]
    [InlineData("", "", true, "Settings.Removed")]
    [InlineData("bad endpoint", "key", false, "Settings.InvalidEndpoint")]
    [InlineData("", "key", false, "Settings.InvalidEndpoint")]
    [InlineData("speech-demo", "", false, "Settings.ApiKeyRequired")]
    [InlineData("https://westeurope.api.cognitive.microsoft.com", "key", false, "Settings.RegionUnsupported")]
    public async Task ValidateAsync_ValidatesConnectionBeforeNetworking(
        string endpoint, string key, bool success, string messageKey)
    {
        var host = new TestPluginHostServices();
        using var sut = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("endpoint", endpoint);
        await sut.SetSettingValueAsync("api-key", key);
        var result = await sut.ValidateAsync();
        Assert.NotNull(result);
        Assert.Equal(success, result.IsSuccess);
        var expected = messageKey == "Settings.RegionUnsupported"
            ? host.Localization.GetString(messageKey, "westeurope", "centralindia, eastus, northeurope, southeastasia, westus, westus2")
            : host.Localization.GetString(messageKey);
        Assert.Equal(expected, result.Message);
    }

    [Theory]
    [InlineData("centralindia")]
    [InlineData("eastus")]
    [InlineData("northeurope")]
    [InlineData("southeastasia")]
    [InlineData("westus")]
    [InlineData("westus2")]
    public async Task ValidateAsync_AcceptsSupportedMaiRegions(string region)
    {
        var endpoint = $"https://{region}.api.cognitive.microsoft.com";
        Assert.Null(MicrosoftAiPlugin.GetUnsupportedMaiRegion(new Uri(endpoint)));
        var handler = new CapturingHandler((_, _) => JsonResponse("""{"data":[{"id":"MAI-Transcribe-2"}]}"""));
        using var sut = new MicrosoftAiPlugin(new HttpClient(handler));
        var host = ConfiguredHost(endpoint);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(host.Localization.GetString("Settings.ModelsRefreshed"), result.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_ReportsCatalogAvailability(bool available)
    {
        var host = ConfiguredHost();
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
            available ? JsonResponse("""{"data":[{"id":"mai-transcribe-preview"}]}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound))));
        await sut.ActivateAsync(host);
        var result = await sut.ValidateAsync();
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(host.Localization.GetString(available ? "Settings.ModelsRefreshed" : "Settings.ModelsUnavailable"), result.Message);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == MicrosoftAiPlugin.DefaultModelId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"gpt-5\"}]}")]
    [InlineData("{\"data\":[{\"id\":123}]}")]
    public async Task CatalogFailure_PreservesCacheAndSelection(string response)
    {
        var host = ConfiguredHost();
        host.SetSetting("cachedModels", (string[])["mai-transcribe-preview"]);
        host.SetSetting("selectedModel", "mai-transcribe-preview");
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) => JsonResponse(response))));
        await sut.ActivateAsync(host);
        var modelsBefore = sut.TranscriptionModels.Select(model => model.Id).ToArray();
        var optionsBefore = sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray();
        await ((IModelCatalogProvider)sut).RefreshModelCatalogAsync();
        Assert.Equal("mai-transcribe-preview", sut.SelectedModelId);
        Assert.Equal(["mai-transcribe-preview"], host.GetSetting<IReadOnlyList<string>>("cachedModels"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.Equal(modelsBefore, sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(optionsBefore,
            sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray());
        Assert.Equal("mai-transcribe-preview", host.GetSetting<string>("selectedModel"));
    }

    [Theory]
    [InlineData("api-key", "new-key", false)]
    [InlineData("endpoint", "other-resource", false)]
    [InlineData("api-key", "new-key", true)]
    [InlineData("endpoint", "other-resource", true)]
    [InlineData("api-key", " azure-key ", false)]
    [InlineData("endpoint", "speech-demo", false)]
    public async Task CatalogResponse_AppliesOnlyForUnchangedConnection(
        string setting, string value, bool restoreOriginal)
    {
        var host = ConfiguredHost();
        host.SetSetting("cachedModels", (string[])["mai-transcribe-preview"]);
        host.SetSetting("selectedModel", "mai-transcribe-preview");
        using var handler = new DelayedCatalogHandler();
        using var sut = new MicrosoftAiPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        var original = await sut.GetSettingValueAsync(setting);
        var modelsBefore = sut.TranscriptionModels.Select(model => model.Id).ToArray();
        var optionsBefore = sut.GetSettingDefinitions()[2].Options!.ToArray();
        var refresh = sut.RefreshModelCatalogAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sut.SetSettingValueAsync(setting, value);
        if (restoreOriginal)
            await sut.SetSettingValueAsync(setting, original);
        var notificationsBefore = host.NotifyCapabilitiesChangedCount;
        handler.Response.SetResult(JsonResponse("""{"data":[{"id":"mai-transcribe-new"}]}"""));

        var unchanged = value is " azure-key " or "speech-demo";
        Assert.Equal(unchanged, await refresh.WaitAsync(TimeSpan.FromSeconds(10)));
        if (unchanged)
        {
            Assert.Equal(["mai-transcribe-new"], host.GetSetting<IReadOnlyList<string>>("cachedModels"));
            Assert.Equal(MicrosoftAiPlugin.DefaultModelId, sut.SelectedModelId);
            Assert.Equal(notificationsBefore + 1, host.NotifyCapabilitiesChangedCount);
        }
        else
        {
            Assert.Equal(["mai-transcribe-preview"], host.GetSetting<IReadOnlyList<string>>("cachedModels"));
            Assert.Equal("mai-transcribe-preview", sut.SelectedModelId);
            Assert.Equal("mai-transcribe-preview", host.GetSetting<string>("selectedModel"));
            Assert.Equal(modelsBefore, sut.TranscriptionModels.Select(model => model.Id).ToArray());
            Assert.Equal(optionsBefore, sut.GetSettingDefinitions()[2].Options!.ToArray());
            Assert.Equal(notificationsBefore, host.NotifyCapabilitiesChangedCount);
        }
    }

    [Fact]
    public async Task CatalogSuccess_RepairsRemovedSelectionAndUpdatesDropdown()
    {
        var host = ConfiguredHost();
        host.SetSetting("cachedModels", (string[])["mai-transcribe-old"]);
        host.SetSetting("selectedModel", "mai-transcribe-old");
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"data":[{"id":" mai-transcribe-new "},{"id":"MAI-TRANSCRIBE-NEW"}]}"""))));
        await sut.ActivateAsync(host);
        await ((IModelCatalogProvider)sut).RefreshModelCatalogAsync();
        Assert.Equal(MicrosoftAiPlugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(sut.SelectedModelId, host.GetSetting<string>("selectedModel"));
        Assert.Equal(3, sut.TranscriptionModels.Count);
        Assert.Contains(sut.GetSettingDefinitions()[2].Options!, option => option.Value == "mai-transcribe-new");
        Assert.DoesNotContain(sut.TranscriptionModels, model => model.Id == "mai-transcribe-old");
    }

    [Fact]
    public void SettingsDefinitions_UseInjectedLocalizationAndExpectedKindsAndOrder()
    {
        using var sut = new MicrosoftAiPlugin();
        var localization = new PluginLocalization(PluginDirectory(), "de");
        sut.SetLocalization(localization);
        var definitions = sut.GetSettingDefinitions();
        Assert.Equal(["api-key", "endpoint", "selectedModel", "transcriptStyle", "speakerDiarizationEnabled"],
            definitions.Select(definition => definition.Key).ToArray());
        Assert.Equal([PluginSettingKind.Secret, PluginSettingKind.Text, PluginSettingKind.Dropdown,
            PluginSettingKind.Dropdown, PluginSettingKind.Boolean], definitions.Select(definition => definition.Kind).ToArray());
        Assert.True(definitions[0].IsSecret);
        Assert.Equal(localization.GetString("Settings.ApiKey"), definitions[0].Label);
        Assert.Equal(localization.GetString("Settings.EndpointPlaceholder"), definitions[1].Placeholder);
        Assert.All(definitions, definition => Assert.False(string.IsNullOrWhiteSpace(definition.Description)));
        Assert.Equal(["clean", "verbatim"], definitions[3].Options!.Select(option => option.Value).ToArray());
        Assert.Equal(LanguageSelectionSupport.Supported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.ExplicitSelectionSupport);
    }

    [Fact]
    public void Localization_AllLocalesHaveMatchingTranslatedKeysAndPlaceholders()
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Join(PluginDirectory(), "Localization", "en.json")))!;
        foreach (var locale in (string[])["en", "de", "es", "ru"])
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Join(PluginDirectory(), "Localization", locale + ".json")))!;
            Assert.Equal(english.Keys.Order(), values.Keys.Order());
            var localization = new PluginLocalization(PluginDirectory(), locale);
            foreach (var (key, value) in values)
            {
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.NotEqual(key, localization.GetString(key));
            }
            Assert.Equal("Microsoft AI", values["Manifest.Name"]);
            Assert.Contains("{0}", values["Settings.RegionUnsupported"]);
            Assert.Contains("{1}", values["Settings.RegionUnsupported"]);
            Assert.Contains("{0}", values["Error.HttpFailure"]);
            Assert.Contains("{1}", values["Error.HttpFailure"]);
            Assert.Contains("{0}", values["Transcript.SpeakerLabel"]);
            if (locale == "en")
                continue;
            foreach (var key in english.Keys.Where(key => key.StartsWith("Error.", StringComparison.Ordinal)))
                Assert.NotEqual(english[key], values[key]);
            Assert.NotEqual(english["Manifest.Description"], values["Manifest.Description"]);
            Assert.NotEqual(english["Settings.EndpointHint"], values["Settings.EndpointHint"]);
            Assert.NotEqual(english["Transcript.SpeakerLabel"], values["Transcript.SpeakerLabel"]);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" auto ")]
    public async Task AutomaticLanguage_OmitsLocaleAndUsesCleanCurrentModel(string? language)
    {
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((request, body) =>
        {
            using var definition = ReadDefinition(request);
            var root = definition.RootElement;
            AssertCurrentEnhancedMode(root, "clean");
            Assert.False(root.TryGetProperty("locales", out _));
            Assert.False(root.TryGetProperty("diarization", out _));
            Assert.False(root.TryGetProperty("phraseList", out _));
            Assert.Equal("None", root.GetProperty("profanityFilterMode").GetString());
            return JsonResponse("""{"phrases":[]}""");
        })));
        await sut.ActivateAsync(ConfiguredHost());
        await sut.TranscribeAsync([1], language, false, null, CancellationToken.None);
    }

    [Fact]
    public async Task DictionaryTerms_AreTrimmedDeduplicatedAndClipped()
    {
        string[] prompts = [" TypeWhisper, typewhisper\rAzure\n azure , ",
            string.Join(',', Enumerable.Range(0, 501).Select(index => $"term{index}")),
            new string('a', 10_000) + "," + new string('b', 10_000) + ",overflow"];
        string[][] expected = [["TypeWhisper", "Azure"],
            [.. Enumerable.Range(0, 500).Select(index => $"term{index}")],
            [new string('a', 10_000), new string('b', 10_000)]];
        for (var index = 0; index < prompts.Length; index++)
        {
            var expectedTerms = expected[index];
            Task<string>? definitionJson = null;
            using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((request, _) =>
            {
                var form = Assert.IsType<MultipartFormDataContent>(request.Content);
                var definition = form.First(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "definition");
                definitionJson = definition.ReadAsStringAsync();
                return JsonResponse("""{"phrases":[]}""");
            })));
            await sut.ActivateAsync(ConfiguredHost());
            await sut.TranscribeAsync([1], null, false, prompts[index], CancellationToken.None);
            Assert.NotNull(definitionJson);
            using var document = JsonDocument.Parse(await definitionJson);
            Assert.Equal(expectedTerms, document.RootElement.GetProperty("phraseList").GetProperty("phrases")
                .EnumerateArray().Select(term => term.GetString()).ToArray());
        }
    }

    [Fact]
    public async Task AudioSizeLimit_IsRejectedBeforeNetworking()
    {
        using var sut = new MicrosoftAiPlugin(new HttpClient(new FailIfCalledHandler()));
        await sut.ActivateAsync(ConfiguredHost());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            new byte[MicrosoftAiPlugin.MaximumAudioBytes], null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, error.FailureKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimit_PreservesRetryAfter(bool dateHeader)
    {
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = dateHeader
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(60))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        })));
        await sut.ActivateAsync(ConfiguredHost());
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.TranscribeAsync(
            [1], null, false, null, CancellationToken.None));
        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter.Value.TotalSeconds, 55, 60);
    }

    [Theory]
    [InlineData(null, "Speaker")]
    [InlineData("de", "Sprecher")]
    public void ParseResponse_LocalizesNumericSpeakerLabels(string? locale, string speakerLabel)
    {
        var localization = locale is null ? null : new PluginLocalization(PluginDirectory(), locale);
        var result = MicrosoftAiPlugin.ParseResponse(SuccessResponse, localization);

        Assert.Equal($"{speakerLabel} 1: Hallo Welt\n{speakerLabel} 2: Willkommen", result.Text);
        Assert.Collection(result.Segments,
            segment => Assert.Equal($"{speakerLabel} 1: Hallo Welt", segment.Text),
            segment => Assert.Equal($"{speakerLabel} 2: Willkommen", segment.Text));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"phrases\":null}")]
    [InlineData("{\"combinedPhrases\":null}")]
    [InlineData("{\"phrases\":{}}")]
    public void ParseResponse_RejectsMissingResultArrays(string json)
    {
        var error = Assert.Throws<PluginRequestException>(() => MicrosoftAiPlugin.ParseResponse(json));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Theory]
    [InlineData("{\"phrases\":[]}")]
    [InlineData("{\"combinedPhrases\":[]}")]
    [InlineData("{\"phrases\":null,\"combinedPhrases\":[]}")]
    [InlineData("{\"phrases\":[{\"text\":\"\"}]}")]
    [InlineData("{\"combinedPhrases\":[{\"text\":\"\"}]}")]
    public void ParseResponse_EmptyResultArraysAreSilence(string json)
    {
        var result = MicrosoftAiPlugin.ParseResponse(json);
        Assert.Equal("", result.Text);
        Assert.Empty(result.Segments);
    }

    [Theory]
    [InlineData("{\"phrases\":[{}]}")]
    [InlineData("{\"phrases\":[null]}")]
    [InlineData("{\"phrases\":[1]}")]
    [InlineData("{\"phrases\":[{\"text\":null}]}")]
    [InlineData("{\"phrases\":[{\"text\":1}]}")]
    [InlineData("{\"combinedPhrases\":[{\"channel\":0}]}")]
    [InlineData("{\"combinedPhrases\":[null]}")]
    [InlineData("{\"combinedPhrases\":[1]}")]
    [InlineData("{\"combinedPhrases\":[{\"text\":null}]}")]
    [InlineData("{\"combinedPhrases\":[{\"text\":1}]}")]
    [InlineData("{\"phrases\":[{\"text\":\"valid\",\"speaker\":1}],\"combinedPhrases\":[{}]}")]
    public void ParseResponse_RejectsMalformedPhraseEntries(string json)
    {
        var error = Assert.Throws<PluginRequestException>(() => MicrosoftAiPlugin.ParseResponse(json));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Fact]
    public void ParseResponse_UsesCombinedPhrasesWithoutSpeakers()
    {
        var result = MicrosoftAiPlugin.ParseResponse("""
            {"combinedPhrases":[{"text":" Combined text "},{"text":"Second channel"}],
             "phrases":[{"text":"segment","offsetMilliseconds":1000,"durationMilliseconds":2000,"speaker":"unknown"}]}
            """);
        Assert.Equal("Combined text\nSecond channel", result.Text);
        Assert.Equal(3, result.DurationSeconds);
        Assert.Equal("segment", Assert.Single(result.Segments).Text);
        var error = Assert.Throws<PluginRequestException>(() => MicrosoftAiPlugin.ParseResponse("not json"));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, error.FailureKind);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task CatalogRequestFailure_PreservesCacheSelectionModelsAndDropdown(string failure)
    {
        var host = ConfiguredHost();
        host.SetSetting("cachedModels", (string[])["mai-transcribe-preview"]);
        host.SetSetting("selectedModel", "mai-transcribe-preview");
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
        {
            return failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                "transport" => throw new HttpRequestException(),
                _ => throw new OperationCanceledException(),
            };
        })));
        await sut.ActivateAsync(host);
        var modelsBefore = sut.TranscriptionModels.Select(model => model.Id).ToArray();
        var optionsBefore = sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray();
        using var caller = new CancellationTokenSource();
        Assert.False(await sut.RefreshModelCatalogAsync(caller.Token));
        Assert.False(caller.IsCancellationRequested);
        Assert.Equal("mai-transcribe-preview", sut.SelectedModelId);
        Assert.Equal(["mai-transcribe-preview"], host.GetSetting<IReadOnlyList<string>>("cachedModels"));
        Assert.Equal(modelsBefore, sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(optionsBefore,
            sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray());
        Assert.Equal("mai-transcribe-preview", host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task CatalogCallerCancellation_PropagatesWithoutChangingCache()
    {
        using var cancellation = new CancellationTokenSource();
        var host = ConfiguredHost();
        host.SetSetting("cachedModels", (string[])["mai-transcribe-preview"]);
        host.SetSetting("selectedModel", "mai-transcribe-preview");
        using var sut = new MicrosoftAiPlugin(new HttpClient(new CapturingHandler((_, _) =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler completes during the awaited request before the cancellation source is disposed.
            cancellation.Cancel();
            // ReSharper disable once AccessToDisposedClosure -- the handler completes during the awaited request before the cancellation source is disposed.
            throw new OperationCanceledException(cancellation.Token);
        })));
        await sut.ActivateAsync(host);
        var modelsBefore = sut.TranscriptionModels.Select(model => model.Id).ToArray();
        var optionsBefore = sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IModelCatalogProvider)sut).RefreshModelCatalogAsync(cancellation.Token));
        Assert.Equal("mai-transcribe-preview", sut.SelectedModelId);
        Assert.Equal(["mai-transcribe-preview"], host.GetSetting<IReadOnlyList<string>>("cachedModels"));
        Assert.Equal(modelsBefore, sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal(optionsBefore,
            sut.GetSettingDefinitions().Single(setting => setting.Key == "selectedModel").Options!.ToArray());
        Assert.Equal("mai-transcribe-preview", host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task Activation_RepairsUnknownModelAndClampsPersistedLegacyDiarization()
    {
        var host = ConfiguredHost();
        host.SetSetting("selectedModel", "unknown");
        using var sut = new MicrosoftAiPlugin();
        await sut.ActivateAsync(host);
        Assert.Equal(MicrosoftAiPlugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(sut.SelectedModelId, host.GetSetting<string>("selectedModel"));
        host.SetSetting("selectedModel", MicrosoftAiPlugin.LegacyModelId);
        host.SetSetting("speakerDiarizationEnabled", true);
        using var reloaded = new MicrosoftAiPlugin();
        await reloaded.ActivateAsync(host);
        Assert.Equal("false", await reloaded.GetSettingValueAsync("speakerDiarizationEnabled"));
        Assert.False(host.GetSetting<bool>("speakerDiarizationEnabled"));
    }

    private static JsonDocument ReadDefinition(HttpRequestMessage request)
    {
        var form = Assert.IsType<MultipartFormDataContent>(request.Content);
        var definition = Assert.Single(form, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "definition");
        return JsonDocument.Parse(definition.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static void AssertCurrentEnhancedMode(JsonElement root, string style)
    {
        var enhanced = root.GetProperty("enhancedMode");
        Assert.True(enhanced.GetProperty("enabled").GetBoolean());
        Assert.Equal(MicrosoftAiPlugin.DefaultModelId, enhanced.GetProperty("model").GetString());
        var options = enhanced.GetProperty("modelOptions");
        Assert.Equal("segment", options.GetProperty("timestamps").GetString());
        Assert.Equal(style, options.GetProperty("transcribeStyle").GetString());
        Assert.False(enhanced.TryGetProperty("transcribeStyle", out _));
        Assert.False(enhanced.TryGetProperty("timestamps", out _));
        Assert.False(root.TryGetProperty("transcribeStyle", out _));
    }

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(thisFile)!, "..", "..",
            "plugins", "TypeWhisper.Plugin.MicrosoftAi"));

    private const string SuccessResponse = """
        {
          "durationMilliseconds":2750,
          "combinedPhrases":[{"channel":0,"text":"Hallo Welt Willkommen"}],
          "phrases":[
            {"offsetMilliseconds":250,"durationMilliseconds":1500,"text":"Hallo Welt","locale":"de-DE","speaker":1},
            {"offsetMilliseconds":1750,"durationMilliseconds":1000,"text":"Willkommen","locale":"de-DE","speaker":"2"}
          ]
        }
        """;

    private static TestPluginHostServices ConfiguredHost(
        string endpoint = "https://speech-demo.cognitiveservices.azure.com")
    {
        var host = new TestPluginHostServices
        {
            Secrets = { ["api-key"] = "azure-key" },
        };
        host.SetSetting("endpoint", endpoint);
        return host;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static byte[] LongDurationWav()
    {
        const int byteRate = 10;
        const int dataSize = byteRate * 7200;
        var bytes = new byte[44 + dataSize];
        "RIFF"u8.CopyTo(bytes.AsSpan(0));
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(5).CopyTo(bytes, 24);
        BitConverter.GetBytes(byteRate).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class DelayedCatalogHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("HTTP should not have been called.");
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];

        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public List<(PluginLogLevel Level, string Message)> Logs { get; } = [];

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
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>()
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => Logs.Add((level, message));
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; set; } = new PluginLocalization(PluginDirectory(), "en");
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
