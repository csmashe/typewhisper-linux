using System.Text;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

// CSV import/export for the dictionary. Kept separate from the core entry-management
// logic in DictionaryService.cs because it is a self-contained serialization concern.
public sealed partial class DictionaryService
{
    public string ExportToCsv()
    {
        EnsureCacheLoaded();

        var sb = new StringBuilder();
        sb.AppendLine(
            "EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source"
        );

        List<DictionaryEntry> entries;
        lock (_gate)
        {
            entries = _cache
                .OrderBy(e => e.EntryType)
                .ThenBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var entry in entries)
        {
            sb.Append(CsvEscape(entry.EntryType.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Original));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Replacement ?? string.Empty));
            sb.Append(',');
            sb.Append(CsvEscape(entry.CaseSensitive.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.IsEnabled.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.IsStarred.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Priority.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Source.ToString()));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public int ImportFromCsv(string csv)
    {
        EnsureCacheLoaded();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return 0;
        }

        var rows = ParseCsv(csv);
        if (rows.Count == 0)
        {
            return 0;
        }

        var imported = 0;

        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var startIndex = LooksLikeHeader(rows[0]) ? 1 : 0;
            var existingKeys = newCache
                .Select(DictionaryEntryKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = startIndex; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count < 2)
                {
                    continue;
                }

                if (
                    !Enum.TryParse<DictionaryEntryType>(row[0], true, out var entryType)
                )
                {
                    continue;
                }

                var original = row[1].Trim();
                if (string.IsNullOrWhiteSpace(original))
                {
                    continue;
                }

                var replacement =
                    row.Count > 2 && !string.IsNullOrWhiteSpace(row[2]) ? row[2].Trim() : null;

                if (
                    entryType == DictionaryEntryType.Correction
                    && string.IsNullOrWhiteSpace(replacement)
                )
                {
                    continue;
                }

                // Term rows must not carry a Replacement: a stray value would break
                // DictionaryEntryKey-based de-duplication on re-import.
                if (entryType != DictionaryEntryType.Correction)
                {
                    replacement = null;
                }

                var entry = new DictionaryEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    EntryType = entryType,
                    Original = original,
                    Replacement = replacement,
                    CaseSensitive = ReadBool(row, 3),
                    IsEnabled = row.Count <= 4 || ReadBool(row, 4),
                    IsStarred = ReadBool(row, 5),
                    Priority = ReadInt(row, 6),
                    Source = ReadSource(row, 7)
                };

                if (!existingKeys.Add(DictionaryEntryKey(entry)))
                {
                    continue;
                }

                newCache.Add(entry);
                imported++;
            }

            if (imported > 0)
            {
                SaveToDisk(newCache);
                _cache = newCache;
            }
        }

        if (imported > 0)
        {
            EntriesChanged?.Invoke();
        }

        return imported;
    }

    private static string DictionaryEntryKey(DictionaryEntry entry)
    {
        return $"{entry.EntryType}|{entry.Original.Trim()}|{entry.Replacement?.Trim()}";
    }

    private static bool LooksLikeHeader(List<string> row)
    {
        return row.Count > 0 && string.Equals(row[0], "EntryType", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBool(List<string> row, int index)
    {
        return row.Count > index && bool.TryParse(row[index], out var value) && value;
    }

    private static int ReadInt(List<string> row, int index)
    {
        return row.Count > index && int.TryParse(row[index], out var value)
            ? Math.Clamp(value, 0, 999)
            : 0;
    }

    private static DictionaryEntrySource ReadSource(List<string> row, int index)
    {
        return row.Count > index
               && Enum.TryParse<DictionaryEntrySource>(row[index], true, out var source)
            ? source
            : DictionaryEntrySource.Import;
    }

    private static string CsvEscape(string value)
    {
        if (
            !value.Contains(',')
            && !value.Contains('"')
            && !value.Contains('\n')
            && !value.Contains('\r')
        )
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                switch (ch)
                {
                    case '"' when i + 1 < csv.Length && csv[i + 1] == '"':
                        field.Append('"');
                        i++;
                        break;
                    case '"':
                        inQuotes = false;
                        break;
                    default:
                        field.Append(ch);
                        break;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Any(value => value.Length > 0))
                    {
                        rows.Add(row);
                    }

                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }
}
