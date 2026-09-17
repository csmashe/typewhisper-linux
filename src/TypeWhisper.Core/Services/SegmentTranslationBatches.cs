using System.Text.Json;

namespace TypeWhisper.Core.Services;

public static class SegmentTranslationBatches
{
    private const int MaxSegmentsPerBatch = 32;
    private const int MaxCharactersPerBatch = 8000;

    public static async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        Func<string, CancellationToken, Task<string>> translateBatch,
        CancellationToken ct
    )
    {
        // Blank cues stay as they are; only the rest is sent, with ids = original indices.
        var translated = texts.ToList();
        var pending = Enumerable.Range(0, texts.Count)
            .Where(id => !string.IsNullOrWhiteSpace(texts[id]))
            .ToList();
        for (var start = 0; start < pending.Count;)
        {
            ct.ThrowIfCancellationRequested();
            var count = 0;
            var characters = 0;
            while (start + count < pending.Count && count < MaxSegmentsPerBatch)
            {
                var length = texts[pending[start + count]].Length;
                if (count > 0 && length > MaxCharactersPerBatch - characters)
                {
                    break;
                }

                characters += length;
                count++;
            }

            var ids = pending.GetRange(start, count);
            var json = JsonSerializer.Serialize(ids.Select(id => new { id, text = texts[id] }));
            ct.ThrowIfCancellationRequested();
            var reply = await translateBatch(json, ct);
            ct.ThrowIfCancellationRequested();
            var batch = ParseReply(reply, ids);
            for (var i = 0; i < count; i++)
            {
                translated[ids[i]] = batch[i];
            }

            start += count;
        }

        return translated;
    }

    private static List<string> ParseReply(string reply, List<int> ids)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            throw new SegmentTranslationMismatchException();
        }

        reply = reply.Trim();
        var firstNewline = reply.IndexOf('\n');
        var lastNewline = reply.LastIndexOf('\n');
        if (firstNewline >= 0 && lastNewline > firstNewline)
        {
            var firstLine = reply[..firstNewline].TrimEnd('\r');
            if (firstLine is "```" or "```json" && reply[(lastNewline + 1)..] == "```")
            {
                reply = reply[(firstNewline + 1)..lastNewline].Trim();
            }
        }

        try
        {
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != ids.Count)
            {
                throw new SegmentTranslationMismatchException();
            }

            var translated = new List<string>(ids.Count);
            foreach (var item in root.EnumerateArray())
            {
                if (
                    item.ValueKind != JsonValueKind.Object
                    || item.EnumerateObject().Count() != 2
                    || !item.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt32(out var index)
                    || index != ids[translated.Count]
                    || !item.TryGetProperty("text", out var text)
                    || text.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(text.GetString())
                )
                {
                    throw new SegmentTranslationMismatchException();
                }

                translated.Add(text.GetString()!.Trim());
            }

            return translated;
        }
        catch (JsonException)
        {
            throw new SegmentTranslationMismatchException();
        }
    }
}
