using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.SherpaOnnx;

internal sealed record SherpaDecodeChunk(
    string Text,
    string[]? Tokens = null,
    float[]? Timestamps = null,
    float[]? Durations = null
);

internal delegate SherpaDecodeChunk SherpaDecodeDelegate(float[] audioSamples);

internal readonly record struct SherpaDecodeResult(
    string Text,
    string? DetectedLanguage,
    IReadOnlyList<VocabularyTokenTiming> TokenTimings
);

// Samples [Start, End) are decoded; the chunk contributes words that start in
// [OwnedStart, OwnedEnd). Owned ranges tile the recording without gaps.
internal readonly record struct SherpaChunkWindow(int Start, int End, int OwnedStart, int OwnedEnd);

internal sealed class SherpaDecodeCoordinator
{
    internal const int SampleRate = 16000;
    internal const int MaximumChunkDurationSeconds = 15;
    internal const int MaximumChunkSampleCount = SampleRate * MaximumChunkDurationSeconds;

    // Decoded on each side of a transducer cut so the boundary word has acoustic
    // context in both chunks. Canary chunks do not overlap.
    private const int BoundaryContextMilliseconds = 500;
    internal const int BoundaryContextSampleCount =
        SampleRate * BoundaryContextMilliseconds / 1000;

    // Two Parakeet encoder frames: the same word decoded in neighbouring chunks
    // may start this far apart. A genuinely repeated word cannot.
    private const double BoundaryJitterSeconds = 0.16;

    private const int BoundarySearchDurationSeconds = 2;
    private const int BoundarySearchSampleCount = SampleRate * BoundarySearchDurationSeconds;
    // Matches upstream's Canary cut: a 100 ms window finds a pause between words
    // rather than a stop-consonant gap inside one.
    private const int EnergyWindowMilliseconds = 100;
    private const int EnergyWindowSampleCount = SampleRate * EnergyWindowMilliseconds / 1000;
    private const int EnergySearchStrideMilliseconds = 10;
    private const int EnergySearchStrideSampleCount =
        SampleRate * EnergySearchStrideMilliseconds / 1000;

    // The untimed fallback only removes text the 1 s of shared audio can hold.
    private const int MaximumFallbackOverlapWords = 6;

    private readonly SherpaDecodeDelegate _decode;

    internal SherpaDecodeCoordinator(SherpaDecodeDelegate decode)
    {
        ArgumentNullException.ThrowIfNull(decode);
        _decode = decode;
    }

    internal SherpaDecodeResult Decode(
        float[] audioSamples,
        bool parseCanaryPayload,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(audioSamples);
        ct.ThrowIfCancellationRequested();

        var result = parseCanaryPayload
            ? DecodeCanary(audioSamples, ct)
            : DecodeTransducer(audioSamples, ct);

        // Do not publish a completed aggregate after cancellation raced the final
        // chunk's parsing/stitching work.
        ct.ThrowIfCancellationRequested();
        return result;
    }

    internal static IReadOnlyList<SherpaChunkWindow> PlanChunks(
        float[] audioSamples,
        bool parseCanaryPayload
    ) => CreateChunks(audioSamples, parseCanaryPayload, CancellationToken.None).ToArray();

    private SherpaDecodeResult DecodeCanary(float[] audioSamples, CancellationToken ct)
    {
        // Chunks do not overlap, so a word repeated across a cut is never matched away.
        var parts = new List<string>();
        string? detectedLanguage = null;
        foreach (var chunk in CreateChunks(audioSamples, parseCanaryPayload: true, ct))
        {
            var decoded = DecodeChunk(audioSamples, chunk, ct);
            var result = ParseCanaryResult(decoded.Text);
            if (result.Text.Length > 0)
                parts.Add(result.Text);
            detectedLanguage ??= result.DetectedLanguage;
        }

        return new SherpaDecodeResult(string.Join(' ', parts), detectedLanguage, []);
    }

    private SherpaDecodeResult DecodeTransducer(float[] audioSamples, CancellationToken ct)
    {
        if (audioSamples.Length <= MaximumChunkSampleCount)
        {
            // A single chunk is the whole recording: keep sherpa's text verbatim, and
            // its times need no offset.
            var whole = DecodeChunk(
                audioSamples,
                new SherpaChunkWindow(0, audioSamples.Length, 0, audioSamples.Length),
                ct
            );
            IReadOnlyList<VocabularyTokenTiming> timings =
                whole.Tokens is not null && whole.Timestamps is not null
                    ? TranscriptionTokenTimings.Create(
                        whole.Tokens,
                        whole.Timestamps,
                        whole.Durations,
                        audioSamples.Length / (double)SampleRate
                    )
                    : [];
            return new SherpaDecodeResult(whole.Text.Trim(), null, timings);
        }

        // Words are owned by the chunk whose region they start in, so boundary text is
        // chosen by time rather than by matching words. Once any chunk lacks usable
        // timings, the rest of the recording is joined as text and publishes none.
        var words = new List<TimedWord>();
        string? untimedText = null;
        foreach (var chunk in CreateChunks(audioSamples, parseCanaryPayload: false, ct))
        {
            var decoded = DecodeChunk(audioSamples, chunk, ct);
            var text = decoded.Text.Trim();
            if (untimedText is null)
            {
                var chunkWords = TryCreateTimedWords(decoded, text, chunk);
                if (chunkWords is not null)
                {
                    AppendOwnedWords(words, chunkWords, chunk);
                    continue;
                }

                untimedText = JoinWords(words);
            }

            untimedText = StitchTokenOverlap(untimedText, text);
        }

        if (untimedText is not null)
            return new SherpaDecodeResult(untimedText, null, []);

        var tokenTimings = words.SelectMany(word => word.Tokens).ToArray();
        return new SherpaDecodeResult(
            JoinWords(words),
            null,
            AreOrdered(tokenTimings, audioSamples.Length / (double)SampleRate) ? tokenTimings : []
        );
    }

    private SherpaDecodeChunk DecodeChunk(
        float[] audioSamples,
        SherpaChunkWindow chunk,
        CancellationToken ct
    )
    {
        // sherpa-onnx 1.12.23 exposes only a synchronous Decode call. These
        // checkpoints cannot interrupt that call, but chunking bounds normal
        // uncancellable work and stops before the next native invocation.
        ct.ThrowIfCancellationRequested();
        var samples =
            chunk.Start == 0 && chunk.End == audioSamples.Length
                ? audioSamples
                : audioSamples[chunk.Start..chunk.End];
        var decoded = _decode(samples);
        ct.ThrowIfCancellationRequested();
        return decoded;
    }

    private static IEnumerable<SherpaChunkWindow> CreateChunks(
        float[] audioSamples,
        bool parseCanaryPayload,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        // Preserve the existing single-call path for short recordings, including an
        // empty recording. Only long audio pays the copy/overlap cost.
        var context = parseCanaryPayload ? 0 : BoundaryContextSampleCount;
        var start = 0;
        var ownedStart = 0;
        while (audioSamples.Length - start > MaximumChunkSampleCount)
        {
            ct.ThrowIfCancellationRequested();
            // The context after the cut must still fit in this chunk.
            var latestCut = start + MaximumChunkSampleCount - context;
            var cut = FindLowEnergyCut(audioSamples, ownedStart, latestCut);
            yield return new SherpaChunkWindow(start, cut + context, ownedStart, cut);
            ownedStart = cut;
            start = cut - context;
        }

        ct.ThrowIfCancellationRequested();
        yield return new SherpaChunkWindow(start, audioSamples.Length, ownedStart, audioSamples.Length);
    }

    private static int FindLowEnergyCut(float[] audioSamples, int ownedStart, int latestCut)
    {
        var searchStart = Math.Max(
            ownedStart + EnergyWindowSampleCount,
            latestCut - BoundarySearchSampleCount
        );
        const int halfWindow = EnergyWindowSampleCount / 2;
        var bestCut = latestCut;
        var bestEnergy = double.MaxValue;

        for (
            var candidate = searchStart;
            candidate <= latestCut;
            candidate += EnergySearchStrideSampleCount
        )
        {
            var windowStart = Math.Max(0, candidate - halfWindow);
            var windowEnd = Math.Min(audioSamples.Length, candidate + halfWindow);
            double energy = 0;
            for (var i = windowStart; i < windowEnd; i++)
                energy += audioSamples[i] * audioSamples[i];

            energy /= Math.Max(1, windowEnd - windowStart);
            // <= so a tie (digital silence across the search window) keeps the
            // latest candidate, producing the longest chunk instead of the shortest.
            // ReSharper disable once InvertIf -- inverting would add a `continue` to a two-line accumulator body.
            if (energy <= bestEnergy)
            {
                bestEnergy = energy;
                bestCut = candidate;
            }
        }

        return bestCut;
    }

    // Groups a chunk's tokens into words with absolute times. Returns null when the
    // timings or durations are unusable or the tokens do not spell sherpa's own text,
    // since the merged transcript is rebuilt from tokens.
    private static List<TimedWord>? TryCreateTimedWords(
        SherpaDecodeChunk decoded,
        string text,
        SherpaChunkWindow chunk
    )
    {
        // Reconciling chunks needs decoded word extents; inferred ends are guesses.
        if (
            decoded.Tokens is null
            || decoded.Timestamps is null
            || decoded.Durations?.Length != decoded.Tokens.Length
        )
            return null;
        if (decoded.Tokens.Length == 0)
            return text.Length == 0 ? [] : null;
        if (
            !string.Equals(
                CollapseWhitespace(string.Concat(decoded.Tokens).Replace('▁', ' ')),
                CollapseWhitespace(text),
                StringComparison.Ordinal
            )
        )
            return null;

        var chunkSeconds = (chunk.End - chunk.Start) / (double)SampleRate;
        var timings = TranscriptionTokenTimings.Create(
            decoded.Tokens,
            decoded.Timestamps,
            decoded.Durations,
            chunkSeconds
        );
        if (timings.Length != decoded.Tokens.Length)
            return null;

        var offset = chunk.Start / (double)SampleRate;
        var groups = new List<List<(VocabularyTokenTiming Timing, double AcousticEnd)>>();
        for (var i = 0; i < timings.Length; i++)
        {
            var timing = timings[i];
            var absolute = timing with
            {
                StartSeconds = timing.StartSeconds + offset,
                EndSeconds = timing.EndSeconds + offset,
            };
            if (
                groups.Count == 0
                || (timing.Text.Length > 0 && (timing.Text[0] == '▁' || char.IsWhiteSpace(timing.Text[0])))
            )
                groups.Add([]);

            // Without a positive duration, the last token is stretched to the chunk's
            // end. That end is not evidence of sound, so reconciliation ignores it.
            var inferredToChunkEnd =
                !(float.IsFinite(decoded.Durations[i]) && decoded.Durations[i] > 0)
                && timing.EndSeconds >= chunkSeconds;
            groups[^1].Add(
                (absolute, inferredToChunkEnd ? absolute.StartSeconds : absolute.EndSeconds)
            );
        }

        return groups.Select(group => new TimedWord(group)).Where(word => word.Text.Length > 0).ToList();
    }

    private static void AppendOwnedWords(
        List<TimedWord> accumulated,
        List<TimedWord> chunkWords,
        SherpaChunkWindow chunk
    )
    {
        var ownedStart = chunk.OwnedStart / (double)SampleRate;
        var ownedEnd = chunk.OwnedEnd / (double)SampleRate;
        var earlierChunkEnd = (chunk.OwnedStart + BoundaryContextSampleCount) / (double)SampleRate;
        // A word this close to the chunk's first sample may have been clipped there, so
        // any overlap with a kept word means the earlier chunk heard it whole.
        var clippedBefore = chunk.Start / (double)SampleRate + BoundaryJitterSeconds;
        var previousCount = accumulated.Count;
        var matched = new HashSet<int>();
        var earliestChanged = double.MaxValue;
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the body mutates accumulated and matched as it goes; a filtered query would hide that.
        foreach (var word in chunkWords)
        {
            if (word.Start >= ownedEnd)
                continue;
            // A word on the cut can be timed either side of it by the two chunks, so
            // each word the earlier chunk kept cancels at most one later copy of itself.
            if (
                word.Start < ownedStart + BoundaryJitterSeconds
                && TryMatchBoundaryDuplicate(accumulated, previousCount, matched, word)
            )
                continue;
            // Before the cut, the later chunk only fills gaps or completes a word the
            // earlier chunk heard truncated.
            if (word.Start < ownedStart)
            {
                var overlapped = FindOverlappingKeptWord(
                    accumulated,
                    previousCount,
                    matched,
                    word,
                    word.Start < clippedBefore ? 0 : BoundaryJitterSeconds
                );
                if (overlapped >= 0)
                {
                    var kept = accumulated[overlapped];
                    if (
                        kept.AcousticEnd < earlierChunkEnd - BoundaryJitterSeconds
                        || word.AcousticEnd <= kept.AcousticEnd
                    )
                    {
                        // The earlier copy stands, and cannot also cancel a later word.
                        matched.Add(overlapped);
                        continue;
                    }
                    accumulated[overlapped] = word;
                    matched.Add(overlapped);
                    earliestChanged = Math.Min(earliestChanged, word.Start);
                    continue;
                }
            }

            accumulated.Add(word);
            earliestChanged = Math.Min(earliestChanged, word.Start);
        }

        // A gap-filling or completed word can start before words the earlier chunk kept.
        var firstOutOfOrder = accumulated.Count;
        while (firstOutOfOrder > 0 && accumulated[firstOutOfOrder - 1].Start >= earliestChanged)
            firstOutOfOrder--;
        var tail = accumulated[firstOutOfOrder..].OrderBy(word => word.Start).ToArray();
        accumulated.RemoveRange(firstOutOfOrder, tail.Length);
        accumulated.AddRange(tail);
    }

    // Matches the earliest unmatched kept copy, so repeated words pair up in order.
    private static bool TryMatchBoundaryDuplicate(
        List<TimedWord> accumulated,
        int previousCount,
        HashSet<int> matched,
        TimedWord word
    )
    {
        var first = previousCount;
        while (first > 0 && accumulated[first - 1].Start >= word.Start - BoundaryJitterSeconds)
            first--;
        for (var i = first; i < previousCount; i++)
        {
            var kept = accumulated[i];
            if (
                Math.Abs(kept.Start - word.Start) <= BoundaryJitterSeconds
                && string.Equals(kept.Key, word.Key, StringComparison.Ordinal)
                && matched.Add(i)
            )
                return true;
        }

        return false;
    }

    private static int FindOverlappingKeptWord(
        List<TimedWord> accumulated,
        int previousCount,
        HashSet<int> matched,
        TimedWord word,
        double neighbourOverlapSeconds
    )
    {
        for (var i = previousCount - 1; i >= 0; i--)
        {
            var kept = accumulated[i];
            // No word lasts a whole chunk, so older words cannot reach this one.
            if (kept.Start < word.Start - MaximumChunkDurationSeconds)
                return -1;
            if (matched.Contains(i))
                continue;
            // A kept word that already cancelled its copy is accounted for. A small
            // overlap is a neighbouring word unless it is this word.
            var overlap =
                Math.Min(kept.AcousticEnd, word.AcousticEnd) - Math.Max(kept.Start, word.Start);
            if (
                overlap > neighbourOverlapSeconds
                || (overlap > 0 && string.Equals(kept.Key, word.Key, StringComparison.Ordinal))
            )
                return i;
        }

        return -1;
    }

    private static bool AreOrdered(VocabularyTokenTiming[] timings, double audioSeconds)
    {
        // ReSharper disable once LoopCanBeConvertedToQuery -- each timing is checked against its predecessor by index; a query would obscure that.
        for (var i = 0; i < timings.Length; i++)
        {
            var timing = timings[i];
            if (
                !(timing.EndSeconds > timing.StartSeconds)
                || timing.EndSeconds > audioSeconds
                || (i > 0 && timing.StartSeconds < timings[i - 1].StartSeconds)
            )
                return false;
        }

        return true;
    }

    private static string JoinWords(List<TimedWord> words) =>
        string.Join(' ', words.Select(word => word.Text));

    private static string CollapseWhitespace(string text) => string.Join(' ', SplitTokens(text));

    // Untimed fallback: drop the longest run (up to the shared audio's capacity) that
    // ends the accumulated text and begins the next chunk, ignoring case and edge
    // punctuation the cut may have changed.
    private static string StitchTokenOverlap(string accumulated, string next)
    {
        if (string.IsNullOrWhiteSpace(accumulated))
            return next.Trim();
        if (string.IsNullOrWhiteSpace(next))
            return accumulated.Trim();

        var accumulatedTokens = SplitTokens(accumulated);
        var nextTokens = SplitTokens(next);
        var maximumOverlap = Math.Min(
            MaximumFallbackOverlapWords,
            Math.Min(accumulatedTokens.Length, nextTokens.Length)
        );
        var overlap = 0;

        for (var length = maximumOverlap; length > 0; length--)
        {
            var matches = true;
            for (var i = 0; i < length; i++)
            {
                // ReSharper disable once InvertIf -- already the inverted mismatch guard; inverting again would re-nest the loop body.
                if (
                    !string.Equals(
                        WordKey(accumulatedTokens[accumulatedTokens.Length - length + i]),
                        WordKey(nextTokens[i]),
                        StringComparison.Ordinal
                    )
                )
                {
                    matches = false;
                    break;
                }
            }

            // ReSharper disable once InvertIf -- the positive form states the "overlap found" case that ends the search.
            if (matches)
            {
                overlap = length;
                break;
            }
        }

        return string.Join(' ', accumulatedTokens.Concat(nextTokens.Skip(overlap)));
    }

    private static string[] SplitTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string WordKey(string word) =>
        word.Trim().Trim(s_punctuationToTrim).Replace('’', '\'').ToUpperInvariant();

    private static readonly char[] s_punctuationToTrim =
        ['.', ',', '!', '?', ';', ':', '"', '\'', '“', '”', '‘', '’', '¿', '¡', '…', '(', ')'];

    private sealed class TimedWord(List<(VocabularyTokenTiming Timing, double AcousticEnd)> tokens)
    {
        internal VocabularyTokenTiming[] Tokens { get; } =
            tokens.Select(token => token.Timing).ToArray();
        internal double Start => Tokens[0].StartSeconds;
        internal double AcousticEnd { get; } = tokens.Max(token => token.AcousticEnd);
        internal string Text { get; } =
            string.Concat(tokens.Select(token => token.Timing.Text)).Replace('▁', ' ').Trim();
        internal string Key => WordKey(Text);
    }

    private static SherpaDecodeResult ParseCanaryResult(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new SherpaDecodeResult(string.Empty, null, []);

        try
        {
            using var json = JsonDocument.Parse(rawText);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return new SherpaDecodeResult(rawText.Trim(), null, []);

            // GetString() throws InvalidOperationException on a number or boolean,
            // which the JsonException handler below would not catch.
            var text = rawText.Trim();
            if (json.RootElement.TryGetProperty("text", out var textNode)
                && textNode.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                text = textNode.GetString()?.Trim() ?? string.Empty;
            }

            string? language = null;
            // ReSharper disable once InvertIf -- the positive TryGetProperty form reads better than an inverted skip.
            // ValueKind guard: GetString() throws on a non-string element, so a canary payload
            // with a numeric or boolean "lang" must fall back rather than fault the decode.
            if (json.RootElement.TryGetProperty("lang", out var languageNode)
                && languageNode.ValueKind == JsonValueKind.String)
            {
                var parsed = languageNode.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    language = parsed;
            }

            return new SherpaDecodeResult(text, language, []);
        }
        catch (JsonException)
        {
            return new SherpaDecodeResult(rawText.Trim(), null, []);
        }
    }
}
