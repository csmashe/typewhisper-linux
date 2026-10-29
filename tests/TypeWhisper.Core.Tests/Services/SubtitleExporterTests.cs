using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class SubtitleExporterTests
{
    [Fact]
    public void ToSrt_SegmentPast24Hours_UsesTotalElapsedHours()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("late cue", 90001.5, 90002.5),
        };

        var srt = SubtitleExporter.ToSrt(segments);

        Assert.Contains("25:00:01,500 --> 25:00:02,500", srt);
    }

    [Fact]
    public void ToWebVtt_SegmentPast24Hours_UsesTotalElapsedHours()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("late cue", 90001.5, 90002.5),
        };

        var vtt = SubtitleExporter.ToWebVtt(segments);

        Assert.Contains("25:00:01.500 --> 25:00:02.500", vtt);
    }

    [Fact]
    public void Export_RoundsToNearestMillisecond()
    {
        var segments = new List<TranscriptionSegment> { new("short cue", 0.042, 0.043) };

        Assert.Contains("00:00:00,042 --> 00:00:00,043", SubtitleExporter.ToSrt(segments));
        Assert.Contains("00:00:00.042 --> 00:00:00.043", SubtitleExporter.ToWebVtt(segments));
    }

    [Fact]
    public void Export_NegativeStart_ClampsToZero()
    {
        var segments = new List<TranscriptionSegment> { new("early cue", -1, 1.125) };

        Assert.Contains("00:00:00,000 --> 00:00:01,125", SubtitleExporter.ToSrt(segments));
        Assert.Contains("00:00:00.000 --> 00:00:01.125", SubtitleExporter.ToWebVtt(segments));
    }

    [Fact]
    public void Export_EndBeforeStart_EndsJustAfterStart()
    {
        var segments = new List<TranscriptionSegment> { new("reversed cue", 2, 1) };

        Assert.Contains("00:00:02,000 --> 00:00:02,001", SubtitleExporter.ToSrt(segments));
        Assert.Contains("00:00:02.000 --> 00:00:02.001", SubtitleExporter.ToWebVtt(segments));
    }

    [Fact]
    public void Export_TimesRoundingToSameMillisecond_EndsJustAfterStart()
    {
        var segments = new List<TranscriptionSegment> { new("tiny cue", 1.0001, 1.0004) };

        Assert.Contains("00:00:01,000 --> 00:00:01,001", SubtitleExporter.ToSrt(segments));
        Assert.Contains("00:00:01.000 --> 00:00:01.001", SubtitleExporter.ToWebVtt(segments));
    }

    [Fact]
    public void Export_NegativeStartAndEnd_EndsJustAfterZero()
    {
        var segments = new List<TranscriptionSegment> { new("pre-roll cue", -2, -1) };

        Assert.Contains("00:00:00,000 --> 00:00:00,001", SubtitleExporter.ToSrt(segments));
        Assert.Contains("00:00:00.000 --> 00:00:00.001", SubtitleExporter.ToWebVtt(segments));
    }

    [Fact]
    public void ToSrt_SegmentsOutOfOrder_NumbersCuesInStartOrder()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("later", 5, 6),
            new("earlier", 2, 3),
        };

        var srt = SubtitleExporter.ToSrt(segments);

        Assert.Equal(
            "1\n00:00:02,000 --> 00:00:03,000\nearlier\n\n2\n00:00:05,000 --> 00:00:06,000\nlater\n\n",
            srt.ReplaceLineEndings("\n")
        );
    }

    [Fact]
    public void ToWebVtt_SegmentsOutOfOrder_WritesCuesInStartOrder()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("later", 5, 6),
            new("earlier", 2, 3),
        };

        var vtt = SubtitleExporter.ToWebVtt(segments);

        Assert.Equal(
            "WEBVTT\n\n00:00:02.000 --> 00:00:03.000\nearlier\n\n00:00:05.000 --> 00:00:06.000\nlater\n\n",
            vtt.ReplaceLineEndings("\n")
        );
    }

    [Fact]
    public void Export_SegmentsWithSameStart_KeepProviderOrder()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("first", 1, 3),
            new("second", 1, 2),
        };

        var vtt = SubtitleExporter.ToWebVtt(segments);

        Assert.True(vtt.IndexOf("first", StringComparison.Ordinal) < vtt.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void ToSrt_SegmentUnder24Hours_FormatsNormally()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("normal cue", 3661.25, 3662.25),
        };

        var srt = SubtitleExporter.ToSrt(segments);

        Assert.Contains("01:01:01,250 --> 01:01:02,250", srt);
    }

    [Fact]
    public void ToWebVtt_SegmentUnder24Hours_FormatsNormally()
    {
        var segments = new List<TranscriptionSegment>
        {
            new("normal cue", 3661.25, 3662.25),
        };

        var vtt = SubtitleExporter.ToWebVtt(segments);

        Assert.Contains("01:01:01.250 --> 01:01:02.250", vtt);
    }
}
