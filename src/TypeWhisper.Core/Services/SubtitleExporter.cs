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
        var number = 1;
        foreach (var (start, end, text) in Cues(segments))
        {
            sb.AppendLine(number++.ToString());
            sb.Append(FormatSrtTime(start));
            sb.Append(" --> ");
            sb.AppendLine(FormatSrtTime(end));
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToWebVtt(IReadOnlyList<TranscriptionSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var (start, end, text) in Cues(segments))
        {
            sb.Append(FormatVttTime(start));
            sb.Append(" --> ");
            sb.AppendLine(FormatVttTime(end));
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // Remote providers can return segments out of order; players expect cues sorted by start.
    // OrderBy is stable, so cues sharing a start keep the provider's order.
    private static IEnumerable<(TimeSpan Start, TimeSpan End, string Text)> Cues(
        IReadOnlyList<TranscriptionSegment> segments
    ) =>
        segments
            .Select(segment =>
            {
                var (start, end) = CueTimes(segment);
                return (start, end, segment.Text);
            })
            .OrderBy(cue => cue.start);

    // Remote providers can return negative or reversed times. Clamped after rounding so the
    // end always lands at least 1 ms after the start; players drop zero-length cues.
    private static (TimeSpan Start, TimeSpan End) CueTimes(TranscriptionSegment segment)
    {
        var start = Math.Max(0, ToMilliseconds(segment.Start));
        var end = Math.Max(start + 1, ToMilliseconds(segment.End));
        return (TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end));
    }

    private static string FormatSrtTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";

    private static string FormatVttTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";

    // TimeSpan.FromSeconds truncates ticks, so 0.043 would print as 042.
    private static double ToMilliseconds(double seconds) => Math.Round(seconds * 1000);
}
