using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Renders timestamped <see cref="TranscriptionSegment" />s as SRT or WebVTT subtitle text.
/// </summary>
public static class SubtitleExporter
{
    public static string ToSrt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var (start, end) = CueTimes(seg);
            sb.AppendLine((i + 1).ToString());
            sb.Append(FormatSrtTime(start));
            sb.Append(" --> ");
            sb.AppendLine(FormatSrtTime(end));
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToWebVtt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var seg in segments)
        {
            var (start, end) = CueTimes(seg);
            sb.Append(FormatVttTime(start));
            sb.Append(" --> ");
            sb.AppendLine(FormatVttTime(end));
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Remote providers can return negative or reversed times.
    private static (double Start, double End) CueTimes(TranscriptionSegment segment)
    {
        var start = Math.Max(0, segment.Start);
        return (start, Math.Max(start, segment.End));
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = ToMilliseconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static string FormatVttTime(double seconds)
    {
        var ts = ToMilliseconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    // TimeSpan.FromSeconds truncates ticks, so 0.043 would print as 042.
    private static TimeSpan ToMilliseconds(double seconds) =>
        TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
}