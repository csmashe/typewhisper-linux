using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiLocalFileDtoTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Deserializes_PathOnly()
    {
        var parsed = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(
            """{"path":"/tmp/clip.wav"}""",
            s_options
        );

        Assert.Equal("/tmp/clip.wav", parsed!.Path);
        Assert.Null(parsed.Language);
    }

    [Fact]
    public void UnsupportedExtension_RecognizedByAudioFileService()
    {
        Assert.False(AudioFileService.IsSupported("/tmp/clip.xyz"));
        Assert.True(AudioFileService.IsSupported("/tmp/clip.wav"));
        Assert.True(AudioFileService.IsSupported("/tmp/clip.mp3"));
        Assert.True(AudioFileService.IsSupported("/tmp/clip.flac"));
    }

    [Fact]
    public void MissingPath_RemainsNull()
    {
        var parsed = JsonSerializer.Deserialize<LocalFileTranscribeRequest>("{}", s_options);
        Assert.Null(parsed!.Path);
    }

    [Fact]
    public void TranscriptionOptions_DefaultWhenLocalFilePayloadOmitsThem()
    {
        var payload = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(
            """{"path":"/tmp/clip.wav"}""",
            s_options
        )!;

        var (task, responseFormat) = HttpApiRequestParser.ParseTranscriptionOptions(
            payload.Task,
            payload.ResponseFormat
        );

        Assert.Equal(TranscriptionTask.Transcribe, task);
        Assert.Equal("json", responseFormat);
    }

    [Fact]
    public void TranscriptionOptions_AcceptCaseInsensitiveLocalFilePayloadValues()
    {
        var payload = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(
            """
            {
              "path": "/tmp/clip.wav",
              "task": "TRANSLATE",
              "response_format": "Verbose_JSON"
            }
            """,
            s_options
        )!;

        var (task, responseFormat) = HttpApiRequestParser.ParseTranscriptionOptions(
            payload.Task,
            payload.ResponseFormat
        );

        Assert.Equal(TranscriptionTask.Translate, task);
        Assert.Equal("verbose_json", responseFormat);
    }

    [Theory]
    [InlineData("task", "transalte", "transcribe", "translate")]
    [InlineData("response_format", "xml", "json", "verbose_json")]
    public void TranscriptionOptions_RejectUnknownLocalFilePayloadValues(
        string field,
        string value,
        string firstAllowed,
        string secondAllowed
    )
    {
        var json = $"{{\"path\":\"/tmp/clip.wav\",\"{field}\":\"{value}\"}}";
        var payload = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(
            json,
            s_options
        )!;

        var ex = Assert.Throws<HttpApiRequestException>(() =>
            HttpApiRequestParser.ParseTranscriptionOptions(
                payload.Task,
                payload.ResponseFormat
            )
        );

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
        Assert.Contains(firstAllowed, ex.Message, StringComparison.Ordinal);
        Assert.Contains(secondAllowed, ex.Message, StringComparison.Ordinal);
    }
}
