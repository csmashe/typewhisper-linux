using System.Net;
using System.Text;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiTranscriptionHelperTests
{
    [Fact]
    public void ParseTranscriptionResponse_MissingText_ThrowsProtocolFailure()
    {
        const string json = """{"language":"en","duration":1.0}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.ParseTranscriptionResponse(json));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json, exception.Message);
    }

    [Fact]
    public void ParseTranscriptionResponse_NonStringText_ThrowsProtocolFailure()
    {
        const string json = """{"text":42,"language":"en","duration":1.0}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.ParseTranscriptionResponse(json));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json, exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_SuccessfulErrorObject_SurfacesProviderMessage()
    {
        const string json = """
                            {
                                "error": {
                                    "message": "The audio format is not supported.",
                                    "type": "invalid_request_error"
                                }
                            }
                            """;
        using var httpClient = new HttpClient(new JsonResponseHandler(json));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.TranscribeAsync(
                httpClient,
                "https://example.test",
                "test-key",
                "test-model",
                [],
                null,
                false,
                "json",
                CancellationToken.None
            ));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("The audio format is not supported.", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json.Length > 200 ? json[..200] : json, exception.Message);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ExtractsNoSpeechProb()
    {
        const string json = """
                            {
                                "text": "So.",
                                "language": "en",
                                "duration": 2.5,
                                "segments": [
                                    { "text": "So.", "start": 0.0, "end": 0.7, "no_speech_prob": 0.95 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("So.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.9f);
        Assert.Single(result.Segments);
        Assert.Equal("So.", result.Segments[0].Text);
        Assert.Equal(0.7, result.Segments[0].End, 0.01);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ReturnsMinNoSpeechProb()
    {
        // Uses min so that mixed speech/silence audio is NOT filtered out
        const string json = """
                            {
                                "text": "Hello world. So.",
                                "language": "en",
                                "duration": 5.0,
                                "segments": [
                                    { "text": "Hello world.", "no_speech_prob": 0.1 },
                                    { "text": "So.", "no_speech_prob": 0.92 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.Equal(0.1f, result.NoSpeechProbability.Value, 0.01f);
    }

    [Fact]
    public void ParseTranscriptionResponse_AllSegmentsSilence_ReturnsHighProb()
    {
        const string json = """
                            {
                                "text": "So. Vorsicht!",
                                "language": "en",
                                "duration": 3.0,
                                "segments": [
                                    { "text": "So.", "no_speech_prob": 0.95 },
                                    { "text": "Vorsicht!", "no_speech_prob": 0.88 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.8f);
    }

    [Fact]
    public void ParseTranscriptionResponse_JsonFormat_NoSegments_ReturnsNull()
    {
        const string json = """
                            {
                                "text": "Hello world",
                                "language": "en",
                                "duration": 2.0
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("Hello world", result.Text);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_EmptySegments_ReturnsNull()
    {
        const string json = """
                            {
                                "text": "",
                                "language": "en",
                                "duration": 1.0,
                                "segments": []
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_LowNoSpeechProb_IndicatesSpeech()
    {
        const string json = """
                            {
                                "text": "This is a normal sentence.",
                                "language": "en",
                                "duration": 3.0,
                                "segments": [
                                    { "text": "This is a normal sentence.", "no_speech_prob": 0.02 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability < 0.1f);
    }

    private sealed class JsonResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
