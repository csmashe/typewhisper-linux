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
            sb.AppendLine((i + 1).ToString());
            sb.Append(FormatSrtTime(seg.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatSrtTime(seg.End));
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
            sb.Append(FormatVttTime(seg.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatVttTime(seg.End));
            sb.AppendLine(seg.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    private static string FormatVttTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }
}