using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class WatchFolderExportBuilderTests
{
    [Fact]
    public void Build_creates_markdown_with_metadata()
    {
        var artifact = WatchFolderExportBuilder.Build(
            WatchFolderOutputFormat.Markdown,
            Result("hello world"),
            "recording.wav",
            "Local / base",
            new DateTime(2026, 4, 28, 12, 0, 0));

        Assert.Equal("md", artifact.FileExtension);
        Assert.Contains("# Transcription: recording.wav", artifact.Content);
        Assert.Contains("- Engine: Local / base", artifact.Content);
        Assert.Contains("hello world", artifact.Content);
    }

    [Fact]
    public void Build_creates_plain_text()
    {
        var artifact = WatchFolderExportBuilder.Build(
            WatchFolderOutputFormat.PlainText,
            Result("plain"),
            "recording.wav",
            "Mock",
            DateTime.Now);

        Assert.Equal("txt", artifact.FileExtension);
        Assert.Equal("plain", artifact.Content);
    }

    [Fact]
    public void Build_creates_subtitle_exports()
    {
        var result = Result("caption") with
        {
            Segments = [new TranscriptionSegment("caption", 1.2, 3.4)]
        };

        var srt = WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Srt, result, "audio.wav", "Mock", DateTime.Now);
        var vtt = WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Vtt, result, "audio.wav", "Mock", DateTime.Now);

        Assert.Equal("srt", srt.FileExtension);
        Assert.Contains("00:00:01,200 --> 00:00:03,400", srt.Content);
        Assert.Equal("vtt", vtt.FileExtension);
        Assert.StartsWith("WEBVTT", vtt.Content);
        Assert.Contains("00:00:01.200 --> 00:00:03.400", vtt.Content);
    }

    [Fact]
    public void Build_rejects_subtitle_export_without_segments()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WatchFolderExportBuilder.Build(WatchFolderOutputFormat.Srt, Result("caption"), "audio.wav", "Mock", DateTime.Now));
    }

    private static WatchFolderTranscriptionResult Result(string text) =>
        new(text, "en", 1.0, 0.5, [], "mock", "base");
}
