using System.Text.Json;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiLocalFileDtoTests
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
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
}
