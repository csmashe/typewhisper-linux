using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

// Stateless rendering of history records to text/CSV/Markdown/JSON. Kept separate from the
// record-management and persistence logic in HistoryService.cs as a distinct export concern.
public sealed partial class HistoryService
{
    public string ExportToText(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(l.Header);
        sb.AppendLine($"{l.Exported}: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"{l.Entries}: {records.Count}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine(
                $"[{r.Timestamp:dd.MM.yyyy HH:mm}] {r.AppProcessName ?? "–"} ({r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)"
            );
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToCsv(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(
            string.Join(
                ',',
                Csv.Escape(l.Timestamp),
                Csv.Escape(l.App),
                Csv.Escape(l.Text),
                Csv.Escape(l.Duration),
                Csv.Escape(l.Words),
                Csv.Escape(l.Language)
            )
        );

        foreach (var r in records)
        {
            sb.AppendLine(
                string.Join(
                    ',',
                    Csv.Escape(
                        r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    ),
                    Csv.Escape(r.AppProcessName ?? ""),
                    Csv.Escape(r.FinalText),
                    Csv.Escape(r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)),
                    Csv.Escape(r.WordCount.ToString(CultureInfo.InvariantCulture)),
                    Csv.Escape(r.Language ?? "")
                )
            );
        }

        return sb.ToString();
    }

    public string ExportToMarkdown(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Header}");
        sb.AppendLine();
        sb.AppendLine($"- **{l.Exported}:** {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- **{l.Entries}:** {records.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"## {r.Timestamp:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.AppProcessName))
            {
                sb.AppendLine($"- **{l.App}:** {r.AppProcessName}");
            }

            sb.AppendLine(
                $"- **{l.Duration}:** {r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"
            );
            if (!string.IsNullOrEmpty(r.Language))
            {
                sb.AppendLine($"- **{l.Language}:** {r.Language}");
            }

            sb.AppendLine();
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToJson(IReadOnlyList<TranscriptionRecord> records)
    {
        var data = records.Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration_seconds = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            insertion_status = r.InsertionStatus.ToString(),
            insertion_failure_reason = r.InsertionFailureReason,
            words = r.WordCount,
        });

        return JsonSerializer.Serialize(data, s_jsonOptions);
    }
}
