using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using GladiaSession = TypeWhisper.Plugin.Gladia.GladiaStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public class GladiaPluginTests
{
    private static readonly JsonSerializerOptions s_manifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string DoneResponse =
        """
        {
          "id": "job-123",
          "status": "done",
          "file": {
            "audio_duration": 2.25
          },
          "result": {
            "metadata": {
              "audio_duration": 2.5
            },
            "transcription": {
              "full_transcript": " Hallo Welt ",
              "languages": ["de"],
              "utterances": [
                {
                  "text": "Hallo",
                  "start": 0.1,
                  "end": 1.0,
                  "language": "de"
                },
                {
                  "text": "Welt",
                  "start": 1.1,
                  "end": 2.4,
                  "language": "de"
                }
              ]
            }
          }
        }
        """;

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
                "TypeWhisper.Plugin.Gladia",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_manifestJsonOptions
        );

        var sut = new GladiaPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void GetSettingDefinitions_LocalizesLabels_WithoutActivation()
    {
        // Regression: a disabled plugin is never activated, so _host (and thus the
        // host-provided localization) is null. The loader injects the catalog via
        // IPluginLocalizationAware at load, so the settings panel must still render
        // real labels — not raw keys like "Settings.ApiKey" — before the user
        // enables the plugin.
        var pluginDir = Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "plugins", "TypeWhisper.Plugin.Gladia"
            )
        );

        var sut = new GladiaPlugin();
        // No ActivateAsync — mimics a discovered-but-disabled plugin.
        sut.SetLocalization(new PluginLocalization(pluginDir, "en"));

        var definitions = sut.GetSettingDefinitions();

        Assert.NotEmpty(definitions);
        foreach (var definition in definitions)
        {
            Assert.False(
                definition.Label.StartsWith("Settings.", StringComparison.Ordinal),
                $"Unlocalized label leaked as raw key: {definition.Label}"
            );
            Assert.False(
                (definition.Description ?? string.Empty).StartsWith("Settings.", StringComparison.Ordinal),
                $"Unlocalized description leaked as raw key: {definition.Description}"
            );
        }

        Assert.Contains(definitions, d => d.Label == "API key");
    }

    [Fact]
    public void ManifestNameAndDescription_AreLocalized()
    {
        // The Plugins-list card text (name + description) is resolved from the
        // plugin's catalog via Manifest.Name / Manifest.Description, falling back
        // to the manifest literal. Guard that the keys exist and German is
        // actually translated (not just echoing English / the raw key).
        var pluginDir = Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "plugins", "TypeWhisper.Plugin.Gladia"
            )
        );

        var en = new PluginLocalization(pluginDir, "en");
        var de = new PluginLocalization(pluginDir, "de");

        // Keys are present (GetString echoes the key back on a miss).
        Assert.NotEqual("Manifest.Description", en.GetString("Manifest.Description"));
        Assert.NotEqual("Manifest.Description", de.GetString("Manifest.Description"));

        // German description is a real translation, not the English string.
        Assert.NotEqual(
            en.GetString("Manifest.Description"),
            de.GetString("Manifest.Description")
        );
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestHost { Secrets = { ["api-key"] = "gladia-key" } };

        var sut = new GladiaPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.gladia", sut.PluginId);
        Assert.Equal("gladia", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new GladiaPlugin();
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Fact]
    public async Task TranscribeAsync_UsesUploadInitiateReturnedPollUrlAndDeleteProtocol()
    {
        var seen = new List<string>();
        var handler = new AsyncCapturingHandler(async (request, body, cancellationToken) =>
        {
            seen.Add($"{request.Method} {request.RequestUri}");
            AssertApiKey(request);

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.ToString() == "https://api.gladia.io/v2/upload")
            {
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                Assert.Equal("multipart/form-data", multipart.Headers.ContentType?.MediaType);
                var audio = Assert.Single(multipart);
                Assert.Equal("audio", audio.Headers.ContentDisposition?.Name?.Trim('"'));
                Assert.Equal("audio.wav", audio.Headers.ContentDisposition?.FileName?.Trim('"'));
                Assert.Equal("audio/wav", audio.Headers.ContentType?.MediaType);
                Assert.Equal([1, 2, 3, 4], await audio.ReadAsByteArrayAsync(cancellationToken));
                return JsonResponse(
                    """{ "audio_url": "https://api.gladia.io/file/upload-456" }"""
                );
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.ToString() == "https://api.gladia.io/v2/pre-recorded")
            {
                Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
                using var document = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing initiation body.")
                );
                var root = document.RootElement;
                Assert.Equal(
                    ["audio_url", "language_config"],
                    root.EnumerateObject().Select(property => property.Name).ToArray()
                );
                Assert.Equal(
                    "https://api.gladia.io/file/upload-456",
                    root.GetProperty("audio_url").GetString()
                );
                Assert.Equal(
                    ["de"],
                    root
                        .GetProperty("language_config")
                        .GetProperty("languages")
                        .EnumerateArray()
                        .Select(item => item.GetString()!)
                        .ToArray()
                );
                Assert.False(root.TryGetProperty("prompt", out _));
                return JsonResponse(
                    """
                    {
                      "id": "job-123",
                      "result_url": "https://results.gladia.test/custom/jobs/job-123?token=returned"
                    }
                    """,
                    HttpStatusCode.Created
                );
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.ToString()
                    == "https://results.gladia.test/custom/jobs/job-123?token=returned")
            {
                return JsonResponse(DoneResponse);
            }

            if (request.Method == HttpMethod.Delete
                && request.RequestUri?.ToString()
                    == "https://api.gladia.io/v2/pre-recorded/job-123")
            {
                return JsonResponse("{}", HttpStatusCode.Accepted);
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3, 4],
            "de",
            translate: false,
            prompt: "This has no Gladia mapping.",
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
        Assert.Equal(["Hallo", "Welt"], result.Segments.Select(segment => segment.Text).ToArray());
        Assert.Equal([0.1, 1.1], result.Segments.Select(segment => segment.Start).ToArray());
        Assert.Equal([1.0, 2.4], result.Segments.Select(segment => segment.End).ToArray());
        Assert.Equal(
            [
                "POST https://api.gladia.io/v2/upload",
                "POST https://api.gladia.io/v2/pre-recorded",
                "GET https://results.gladia.test/custom/jobs/job-123?token=returned",
                "DELETE https://api.gladia.io/v2/pre-recorded/job-123",
            ],
            seen
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TranscribeAsync_OmitsLanguageConfigForUnspecifiedLanguage(string? language)
    {
        var handler = new SuccessfulFlowHandler((request, body) =>
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsolutePath != "/v2/pre-recorded")
            {
                return;
            }

            using var document = JsonDocument.Parse(
                body ?? throw new InvalidOperationException("Missing initiation body.")
            );
            Assert.False(document.RootElement.TryGetProperty("language_config", out _));
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            language,
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_MapsExplicitLanguageToLanguageConfig()
    {
        var handler = new SuccessfulFlowHandler((request, body) =>
        {
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.AbsolutePath != "/v2/pre-recorded")
            {
                return;
            }

            using var document = JsonDocument.Parse(
                body ?? throw new InvalidOperationException("Missing initiation body.")
            );
            var languageConfig = document.RootElement.GetProperty("language_config");
            Assert.Equal(
                ["fr"],
                languageConfig
                    .GetProperty("languages")
                    .EnumerateArray()
                    .Select(item => item.GetString()!)
                    .ToArray()
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.TranscribeAsync(
            [1, 2, 3],
            "fr",
            translate: false,
            prompt: null,
            CancellationToken.None
        );
    }

    [Fact]
    public async Task TranscribeAsync_PollsQueuedAndProcessingUntilDone()
    {
        var pollCount = 0;
        var deleteCount = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            AssertApiKey(request);

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                pollCount++;
                return pollCount switch
                {
                    1 => JsonResponse("""{ "status": "queued" }"""),
                    2 => JsonResponse("""{ "status": "processing" }"""),
                    _ => JsonResponse(DoneResponse),
                };
            }

            if (request.Method == HttpMethod.Delete)
            {
                deleteCount++;
                return JsonResponse("{}", HttpStatusCode.Accepted);
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal(3, pollCount);
        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public async Task TranscribeAsync_TerminalErrorIncludesProviderDetailsAndDeletesJob()
    {
        var deleteCount = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(
                    """
                    {
                      "status": "error",
                      "error_code": 422,
                      "error": { "message": "Audio could not be decoded" },
                      "request_id": "G-request-7"
                    }
                    """
                );
            }

            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal(
                    "https://api.gladia.io/v2/pre-recorded/job-123",
                    request.RequestUri?.ToString()
                );
                deleteCount++;
                return JsonResponse("{}", HttpStatusCode.Accepted);
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("status=error", exception.Message);
        Assert.Contains("422", exception.Message);
        Assert.Contains("Audio could not be decoded", exception.Message);
        Assert.Contains("G-request-7", exception.Message);
        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public async Task TranscribeAsync_DoneWithoutFullTranscriptFailsAndDeletesJob()
    {
        var deleteCount = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(
                    """
                    {
                      "status": "done",
                      "result": {
                        "metadata": { "audio_duration": 1.0 },
                        "transcription": { "languages": ["en"] }
                      }
                    }
                    """
                );
            }

            if (request.Method == HttpMethod.Delete)
            {
                deleteCount++;
                return JsonResponse("{}", HttpStatusCode.Accepted);
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("result.transcription.full_transcript", exception.Message);
        Assert.Equal(1, deleteCount);
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("initiate")]
    [InlineData("poll")]
    public async Task TranscribeAsync_HttpErrorAtEachStageFails(string failingStage)
    {
        var requestCount = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            requestCount++;

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return failingStage == "upload"
                    ? JsonResponse(
                        """{ "message": "upload rejected" }""",
                        HttpStatusCode.BadGateway
                    )
                    : JsonResponse(
                        """{ "audio_url": "https://api.gladia.io/file/upload-456" }"""
                    );
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return failingStage == "initiate"
                    ? JsonResponse(
                        """{ "message": "initiation rejected" }""",
                        HttpStatusCode.BadGateway
                    )
                    : InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("poll", failingStage);
                return JsonResponse(
                    """{ "message": "poll rejected" }""",
                    HttpStatusCode.BadGateway
                );
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("502", exception.Message);
        Assert.Contains(
            failingStage switch
            {
                "upload" => "upload rejected",
                "initiate" => "initiation rejected",
                _ => "poll rejected",
            },
            exception.Message
        );
        Assert.Equal(
            failingStage switch
            {
                "upload" => 1,
                "initiate" => 2,
                _ => 3,
            },
            requestCount
        );
    }

    [Theory]
    [InlineData("\"unauthorized\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task TranscribeAsync_NonObjectJsonErrorBodyStillReportsHttpError(string errorBody)
    {
        // Non-object error bodies must not derail HttpRequestException (TryGetProperty would throw).
        var handler = new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post
            && request.RequestUri?.AbsolutePath == "/v2/upload"
                ? JsonResponse(errorBody, HttpStatusCode.BadGateway)
                : throw new InvalidOperationException(
                    $"Unexpected request: {request.Method} {request.RequestUri}"
                ));

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("502", exception.Message);
    }

    [Theory]
    // ReSharper disable once RawStringCanBeSimplified -- kept raw to match the sibling InlineData rows, which need raw strings for their quotes.
    [InlineData("""{}""")]
    [InlineData("""{ "status": 17 }""")]
    [InlineData("""{ "status": "paused" }""")]
    public async Task TranscribeAsync_MalformedOrUnknownPollStatusFails(string pollResponse)
    {
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            return request.Method == HttpMethod.Get
                ? JsonResponse(pollResponse)
                : throw new InvalidOperationException(
                    $"Unexpected request: {request.Method} {request.RequestUri}"
                );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("status", exception.Message);
    }

    [Theory]
    [InlineData(
        """{ "result_url": "https://results.gladia.test/custom/jobs/job-123" }""",
        "id"
    )]
    [InlineData("""{ "id": "job-123" }""", "result_url")]
    public async Task TranscribeAsync_InitiationRequiresIdAndResultUrl(
        string initiationJson,
        string missingField
    )
    {
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            return request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded"
                ? JsonResponse(initiationJson, HttpStatusCode.Created)
                : throw new InvalidOperationException(
                    $"Unexpected request: {request.Method} {request.RequestUri}"
                );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains(missingField, exception.Message);
    }

    [Theory]
    [InlineData("http://results.gladia.test/custom/jobs/job-123?token=returned")]
    [InlineData("ftp://results.gladia.test/custom/jobs/job-123")]
    [InlineData("not-a-url")]
    public async Task TranscribeAsync_RejectsNonHttpsResultUrl(string resultUrl)
    {
        // Non-HTTPS result_url would leak x-gladia-key in plaintext during polling; must be rejected.
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            return request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded"
                ? JsonResponse(
                    $$"""{ "id": "job-123", "result_url": "{{resultUrl}}" }""",
                    HttpStatusCode.Created
                )
                : throw new InvalidOperationException(
                    $"Unexpected request: {request.Method} {request.RequestUri}"
                );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("result_url", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_CancellationDuringPollingIsObserved()
    {
        var pollStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var deleteCount = 0;
        var handler = new AsyncCapturingHandler(async (request, _, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                pollStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonResponse("""{ "status": "processing" }""");
            }

            if (request.Method == HttpMethod.Delete)
            {
                deleteCount++;
                return JsonResponse("{}", HttpStatusCode.Accepted);
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(handler);
        using var cts = new CancellationTokenSource();

        var transcription = sut.TranscribeAsync(
            [1, 2, 3],
            "en",
            translate: false,
            prompt: null,
            cts.Token
        );
        // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; using cts.Token would abort this wait on the cancellation the test triggers next.
        await pollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
        Assert.Equal(0, deleteCount);
    }

    [Fact]
    public async Task TranscribeAsync_BoundedPollingWindowTimesOut()
    {
        var pollCount = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse("""{ "audio_url": "https://api.gladia.io/file/upload-456" }""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get)
            {
                pollCount++;
                return JsonResponse("""{ "status": "processing" }""");
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        });

        using var sut = await CreateConfiguredPluginAsync(
            handler,
            pollDelay: TimeSpan.FromHours(1),
            pollWindow: TimeSpan.FromMilliseconds(20)
        );

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("did not complete", exception.Message);
        Assert.Equal(1, pollCount);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslationBeforeHttp()
    {
        var handler = new CountingHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: true,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("does not support translation", exception.Message);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TranscribeAsync_RequiresConfigurationBeforeHttp()
    {
        var handler = new CountingHandler();
        using var sut = new GladiaPlugin(
            new HttpClient(handler),
            pollDelay: TimeSpan.Zero
        );
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TranscribeAsync_DeleteFailureDoesNotAffectCompletedResult()
    {
        var deleteCount = 0;
        var handler = new SuccessfulFlowHandler(
            inspectRequest: (request, _) =>
            {
                if (request.Method == HttpMethod.Delete)
                    deleteCount++;
            },
            deleteStatusCode: HttpStatusCode.InternalServerError
        );

        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal(1, deleteCount);
    }

    [Fact]
    public void BuildInitRequest_UsesWavPcmAndEnablesPartials()
    {
        var json = GladiaSession.BuildInitRequest("de", 16000);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wav/pcm", root.GetProperty("encoding").GetString());
        Assert.Equal(16, root.GetProperty("bit_depth").GetInt32());
        Assert.Equal(16000, root.GetProperty("sample_rate").GetInt32());
        Assert.True(
            root.GetProperty("messages_config").GetProperty("receive_partial_transcripts").GetBoolean()
        );
        Assert.Equal(
            "de",
            root.GetProperty("language_config").GetProperty("languages")[0].GetString()
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildInitRequest_OmitsLanguageConfig_ForUnspecifiedLanguage(string? language)
    {
        var json = GladiaSession.BuildInitRequest(language, 16000);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("language_config", out _));
    }

    [Fact]
    public void ParseSessionUrl_ExtractsUrl()
    {
        var url = GladiaSession.ParseSessionUrl(
            """{ "id": "abc", "url": "wss://api.gladia.io/v2/live?token=xyz" }"""
        );

        Assert.Equal("wss://api.gladia.io/v2/live?token=xyz", url);
    }

    [Fact]
    public void ParseSessionUrl_ReturnsNull_WhenMissingOrMalformed()
    {
        Assert.Null(GladiaSession.ParseSessionUrl("""{ "id": "abc" }"""));
        Assert.Null(GladiaSession.ParseSessionUrl("not json"));
    }

    [Fact]
    public void ParseMessage_ReadsFinalTranscript()
    {
        var msg = GladiaSession.ParseMessage(
            """
            { "type": "transcript",
              "data": { "is_final": true, "utterance": { "text": " Hello world" } } }
            """
        );

        Assert.Equal("transcript", msg.MessageType);
        Assert.Equal(" Hello world", msg.Text);
        Assert.True(msg.IsFinal);
    }

    [Fact]
    public void ParseMessage_ReadsPartialTranscript()
    {
        var msg = GladiaSession.ParseMessage(
            """
            { "type": "transcript",
              "data": { "is_final": false, "utterance": { "text": "Hello" } } }
            """
        );

        Assert.Equal("Hello", msg.Text);
        Assert.False(msg.IsFinal);
    }

    [Fact]
    public void ParseMessage_IgnoresNonTranscriptTypes()
    {
        var msg = GladiaSession.ParseMessage("""{ "type": "audio_chunk_acknowledgment" }""");

        Assert.Equal("audio_chunk_acknowledgment", msg.MessageType);
        Assert.Null(msg.Text);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var msg = GladiaSession.ParseMessage("garbage {");

        Assert.Equal("", msg.MessageType);
        Assert.Null(msg.Text);
    }

    private static async Task<GladiaPlugin> CreateConfiguredPluginAsync(
        HttpMessageHandler handler,
        TimeSpan? pollDelay = null,
        TimeSpan? pollWindow = null
    )
    {
        var sut = new GladiaPlugin(
            new HttpClient(handler),
            pollDelay ?? TimeSpan.Zero,
            pollWindow
        );
        await sut.ActivateAsync(
            new TestHost
            {
                Secrets =
                {
                    ["api-key"] = "test-key",
                },
            }
        );
        return sut;
    }

    private static void AssertApiKey(HttpRequestMessage request)
    {
        Assert.True(request.Headers.TryGetValues("x-gladia-key", out var values));
        Assert.Equal(["test-key"], values.ToArray());
    }

    private static HttpResponseMessage InitiationResponse() =>
        JsonResponse(
            """
            {
              "id": "job-123",
              "result_url": "https://results.gladia.test/custom/jobs/job-123?token=returned"
            }
            """,
            HttpStatusCode.Created
        );

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class SuccessfulFlowHandler(
        Action<HttpRequestMessage, byte[]?>? inspectRequest = null,
        HttpStatusCode deleteStatusCode = HttpStatusCode.Accepted
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            AssertApiKey(request);
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            inspectRequest?.Invoke(request, body);

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/upload")
            {
                return JsonResponse(
                    """{ "audio_url": "https://api.gladia.io/file/upload-456" }"""
                );
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath == "/v2/pre-recorded")
            {
                return InitiationResponse();
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.ToString()
                    == "https://results.gladia.test/custom/jobs/job-123?token=returned")
            {
                return JsonResponse(DoneResponse);
            }

            if (request.Method == HttpMethod.Delete
                && request.RequestUri?.ToString()
                    == "https://api.gladia.io/v2/pre-recorded/job-123")
            {
                return JsonResponse(
                    deleteStatusCode == HttpStatusCode.Accepted
                        ? "{}"
                        : """{ "message": "cleanup rejected" }""",
                    deleteStatusCode
                );
            }

            throw new InvalidOperationException(
                $"Unexpected request: {request.Method} {request.RequestUri}"
            );
        }
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, byte[]?, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class AsyncCapturingHandler(
        Func<
            HttpRequestMessage,
            byte[]?,
            CancellationToken,
            Task<HttpResponseMessage>
        > responder
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return await responder(request, body, cancellationToken);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            throw new InvalidOperationException(
                $"Unexpected HTTP request: {request.Method} {request.RequestUri}"
            );
        }
    }

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
