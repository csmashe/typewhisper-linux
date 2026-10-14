using System.Buffers.Binary;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.NeedleWhistle;

internal static class NeedleWhistleContract
{
    public static IReadOnlyList<string> BuildArguments(
        string modelPath,
        string audioPath,
        string? language,
        string? keywordsPath
    )
    {
        var arguments = new List<string>
        {
            "--model", modelPath,
            "--audio", audioPath,
            "--audio-word-timestamps",
        };

        if (!string.IsNullOrWhiteSpace(language) && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--audio-language");
            arguments.Add(language.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(keywordsPath))
        {
            arguments.Add("--audio-keywords");
            arguments.Add(keywordsPath);
        }

        return arguments;
    }

    public static PluginTranscriptionResult ParseResult(string standardOutput, double durationSeconds)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Needle returned an invalid transcription response. Output: {TrimForError(standardOutput)}"
                );
            }

            var language = root.TryGetProperty("language", out var languageElement)
                && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString()
                : null;
            var segments = new List<PluginTranscriptionSegment>();
            if (root.TryGetProperty("words", out var wordsElement))
            {
                if (wordsElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("Needle returned a malformed words field.");

                foreach (var word in wordsElement.EnumerateArray())
                {
                    if (word.ValueKind != JsonValueKind.Object
                        || !word.TryGetProperty("word", out var wordText)
                        || wordText.ValueKind != JsonValueKind.String
                        || !word.TryGetProperty("start", out var start)
                        || !start.TryGetDouble(out var startSeconds)
                        || !word.TryGetProperty("end", out var end)
                        || !end.TryGetDouble(out var endSeconds)
                        || !double.IsFinite(startSeconds)
                        || !double.IsFinite(endSeconds)
                        || startSeconds < 0
                        || endSeconds < startSeconds)
                    {
                        throw new InvalidOperationException("Needle returned a malformed word timestamp.");
                    }

                    segments.Add(new PluginTranscriptionSegment(wordText.GetString()!, startSeconds, endSeconds));
                }
            }

            return new PluginTranscriptionResult(
                textElement.GetString()!,
                string.IsNullOrWhiteSpace(language) ? null : language,
                durationSeconds,
                NoSpeechProbability: null
            )
            {
                Segments = segments,
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Needle returned invalid JSON. Output: {TrimForError(standardOutput)}",
                ex
            );
        }
    }

    public static double ReadWavDurationSeconds(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12
            || !wav[..4].SequenceEqual("RIFF"u8)
            || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Needle requires valid RIFF/WAVE audio.");
        }

        int? byteRate = null;
        uint? dataLength = null;
        var offset = 12;
        while (offset <= wav.Length - 8)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4));
            var chunkStart = offset + 8;
            // FFmpeg cannot seek back to fill the data size when it writes WAV
            // to stdout. The complete captured buffer supplies that size instead.
            var openEndedData = chunkSize == uint.MaxValue
                && wav.Slice(offset, 4).SequenceEqual("data"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(4, 4)) == uint.MaxValue;
            if (openEndedData)
                chunkSize = (uint)(wav.Length - chunkStart);
            if (chunkSize > int.MaxValue || chunkStart > wav.Length - (int)chunkSize)
                throw new InvalidDataException("The WAVE file contains a truncated chunk.");

            if (wav.Slice(offset, 4).SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("The WAVE format chunk is too short.");
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(chunkStart + 8, 4));
            }
            else if (wav.Slice(offset, 4).SequenceEqual("data"u8))
            {
                dataLength = chunkSize;
                if (openEndedData) break;
            }

            var paddedSize = (long)chunkSize + (chunkSize & 1);
            var next = (long)chunkStart + paddedSize;
            if (next > int.MaxValue || next > wav.Length)
                break;
            offset = (int)next;
        }

        if (byteRate is null or <= 0 || dataLength is null)
            throw new InvalidDataException("The WAVE file is missing a valid format or data chunk.");

        return dataLength.Value / (double)byteRate.Value;
    }

    private static string TrimForError(string text) => text.Length <= 512 ? text : text[..512] + "…";
}
