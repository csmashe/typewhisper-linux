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
