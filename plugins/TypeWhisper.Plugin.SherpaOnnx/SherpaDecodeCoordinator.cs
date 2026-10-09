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

    // Measured on real dictation: the two chunks time a shared word up to 0.44 s apart
    // (the earlier one early, the later one late), so seams pair words by sequence and
    // use this only to rule out pairing distant repeats.
    private const double MaximumSeamShiftSeconds = 0.5;

    // One Parakeet encoder frame: a word this close to a chunk's edge may be cut off.
    private const double EdgeFrameSeconds = 0.08;

    // Two frames of the drift between the chunks' timings.
    private const double SeamDriftSeconds = 0.16;

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

        // Each seam is reconciled from both chunks' words in the shared audio. Once any
        // chunk lacks usable timings, the rest is joined as text and publishes none.
        var words = new List<TimedWord>();
        SherpaChunkWindow? previous = null;
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
                    if (previous is { } earlier)
                        MergeSeam(words, chunkWords, earlier, chunk);
                    else
                        words.AddRange(chunkWords);
                    previous = chunk;
                    continue;
                }

                untimedText = previous is { } last ? SeedUntimedText(words, last, text) : string.Empty;
            }

            untimedText = StitchTokenOverlap(untimedText, text);
        }

        if (untimedText is not null)
            return new SherpaDecodeResult(untimedText, null, []);

        var tokenTimings = KeepStartsInOrder(words.SelectMany(word => word.Tokens));
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
            // Spread what is left evenly: Parakeet decodes a short final chunk of real
            // speech to nothing.
            var ownedPerChunk = MaximumChunkSampleCount - 2 * context;
            var chunksLeft = (audioSamples.Length - ownedStart + ownedPerChunk - 1) / ownedPerChunk;
            var target = ownedStart + (audioSamples.Length - ownedStart) / chunksLeft;
            var cut = FindLowEnergyCut(
                audioSamples,
                Math.Max(ownedStart + EnergyWindowSampleCount, target - BoundarySearchSampleCount / 2),
                Math.Min(latestCut, target + BoundarySearchSampleCount / 2)
            );
            yield return new SherpaChunkWindow(start, cut + context, ownedStart, cut);
            ownedStart = cut;
            start = cut - context;
        }

        ct.ThrowIfCancellationRequested();
        yield return new SherpaChunkWindow(start, audioSamples.Length, ownedStart, audioSamples.Length);
    }

    private static int FindLowEnergyCut(float[] audioSamples, int searchStart, int latestCut)
    {
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
        // sherpa reports a chunk with no speech as no tokens and no timing arrays.
        if (decoded.Tokens is null || decoded.Tokens.Length == 0)
            return text.Length == 0 ? [] : null;
        // Reconciling chunks needs decoded word extents; inferred ends are guesses.
        if (decoded.Timestamps is null || decoded.Durations?.Length != decoded.Tokens.Length)
            return null;
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

    // Replaces the earlier chunk's words in the shared audio with one reading of it.
    // Words both chunks heard are paired by sequence; the earlier chunk keeps the words
    // before the pair nearest the cut, the later chunk that pair and the rest. With no
    // pair, the cut divides them. On real dictation, also keeping words only the
    // non-owning chunk heard added alternative readings and recovered no lost words.
    private static void MergeSeam(
        List<TimedWord> words,
        List<TimedWord> next,
        SherpaChunkWindow earlier,
        SherpaChunkWindow later
    )
    {
        var cut = later.OwnedStart / (double)SampleRate;
        var sharedStart = later.Start / (double)SampleRate;
        var earlierEnd = earlier.End / (double)SampleRate;

        // By end, so a long word running into the shared audio is reconciled too.
        var first = words.Count;
        while (first > 0 && words[first - 1].KnownEnd > sharedStart - MaximumSeamShiftSeconds)
            first--;
        var a = words[first..];
        var b = next.TakeWhile(word => word.Start < earlierEnd + MaximumSeamShiftSeconds).ToList();

        var pairs = AlignSeam(a, b, sharedStart, earlierEnd);
        var region = new List<TimedWord>();
        if (pairs.Count == 0)
        {
            // Each word goes to the side of the cut most of it lies on. A later word at
            // its chunk's first sample, mostly inside a complete earlier word, is its tail.
            var keptB = b.Where(word =>
                    word.Midpoint >= cut
                    && !(
                        word.Start < sharedStart + EdgeFrameSeconds
                        && a.Exists(other =>
                            other.Start < word.Start
                            && other.EndKnown
                            && other.AcousticEnd < earlierEnd - EdgeFrameSeconds
                            && OverlapSeconds(other, word) > (word.KnownEnd - word.Start) / 2
                        )
                    )
                )
                .ToList();
            // A word read on both sides, or neither, is kept once from the later chunk,
            // which times the shared audio more closely.
            foreach (var rejected in a.Where(word => word.Midpoint >= cut))
            {
                // Already read by a word the later chunk kept.
                if (keptB.Exists(word => OverlapSeconds(rejected, word) > -SeamDriftSeconds))
                    continue;
                var rescued = b.Where(word =>
                        word.Midpoint < cut
                        && !keptB.Contains(word)
                        && word.Start >= sharedStart + EdgeFrameSeconds
                        && Math.Abs(rejected.Start - word.Start) <= MaximumSeamShiftSeconds
                    )
                    .MinBy(word => Math.Abs(rejected.Start - word.Start));
                if (rescued is not null)
                    keptB.Add(rescued);
            }
            keptB = [.. b.Where(keptB.Contains)];
            region.AddRange(
                a.Where(word =>
                    word.Midpoint < cut
                    && !keptB.Exists(other => OverlapSeconds(word, other) > SeamDriftSeconds)
                )
            );
            region.AddRange(keptB);
        }
        else
        {
            var anchor = pairs.MinBy(pair => Math.Abs((a[pair.A].Start + b[pair.B].Start) / 2 - cut));
            region.AddRange(a.Take(anchor.A));
            region.AddRange(b.Skip(anchor.B));

            // Either copy of a pair is the same word; a copy cut off at its chunk's edge
            // gives way to the complete one for its timings.
            foreach (var (i, j) in pairs)
            {
                var clippedA = !a[i].EndKnown || a[i].AcousticEnd >= earlierEnd - EdgeFrameSeconds;
                var clippedB = b[j].Start < sharedStart + EdgeFrameSeconds;
                if (clippedA == clippedB)
                    continue;
                var kept = i < anchor.A ? a[i] : b[j];
                var complete = clippedA ? b[j] : a[i];
                region[region.IndexOf(kept)] = complete;
            }
        }

        words.RemoveRange(first, a.Count);
        words.AddRange(region);
        words.AddRange(next.Skip(b.Count));
    }

    // Longest common subsequence of the two readings, pairing only the same word timed
    // close enough to be one utterance. Among equally long alignments, the one whose
    // pairs are timed closest wins, so a repeat pairs with its own copy.
    private static List<(int A, int B)> AlignSeam(
        List<TimedWord> a,
        List<TimedWord> b,
        double sharedStart,
        double earlierEnd
    )
    {
        // Each pair scores more than any total timing difference can subtract.
        const double pairScore = 1000;
        var scores = new double[a.Count + 1, b.Count + 1];
        var paired = new bool[a.Count, b.Count];
        for (var i = a.Count - 1; i >= 0; i--)
        for (var j = b.Count - 1; j >= 0; j--)
        {
            scores[i, j] = Math.Max(scores[i + 1, j], scores[i, j + 1]);
            if (!IsSameWord(a[i], b[j]))
                continue;
            var withPair = scores[i + 1, j + 1] + pairScore - Math.Abs(a[i].Start - b[j].Start);
            // ReSharper disable once InvertIf -- the positive form records the pairing choice it makes.
            if (withPair >= scores[i, j])
            {
                scores[i, j] = withPair;
                paired[i, j] = true;
            }
        }

        var pairs = new List<(int A, int B)>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (paired[x, y])
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (scores[x + 1, y] >= scores[x, y + 1])
                x++;
            else
                y++;
        }

        return pairs;

        // Copies of one utterance must each lie in audio the other chunk also heard;
        // the later chunk's copies run late by up to the drift tolerance. Copies that
        // overlap pair however far apart they start, as a clipped copy may.
        bool IsSameWord(TimedWord earlier, TimedWord later) =>
            earlier.KnownEnd > sharedStart
            && later.Start < earlierEnd + SeamDriftSeconds
            && (
                Math.Abs(earlier.Start - later.Start) <= MaximumSeamShiftSeconds
                || OverlapSeconds(earlier, later) > 0
            )
            && string.Equals(earlier.Key, later.Key, StringComparison.Ordinal);
    }

    private static double OverlapSeconds(TimedWord a, TimedWord b) =>
        Math.Min(a.KnownEnd, b.KnownEnd) - Math.Max(a.Start, b.Start);

    // Text for the untimed fallback: the timed words so far, plus as much of the last
    // timed chunk's complete words past its cut as the next chunk does not restate.
    private static string SeedUntimedText(List<TimedWord> words, SherpaChunkWindow last, string next)
    {
        var cut = last.OwnedEnd / (double)SampleRate;
        var lastEnd = last.End / (double)SampleRate;
        var ownedCount = words.Count;
        while (ownedCount > 0 && words[ownedCount - 1].Start >= cut)
            ownedCount--;
        var owned = words[..ownedCount];
        // A word sounding up to the chunk's last sample was likely cut off.
        var tail = words[ownedCount..]
            .Where(word => word.EndKnown && word.AcousticEnd < lastEnd - EdgeFrameSeconds)
            .ToArray();

        // The next chunk may restate only part of the tail, or only owned words; seam on
        // the longest tail prefix it overlaps rather than duplicating.
        var tailLength = tail.Length;
        while (tailLength > 0 && CountTokenOverlap(JoinWords([.. owned, .. tail[..tailLength]]), next) == 0)
            tailLength--;
        if (CountTokenOverlap(JoinWords([.. owned, .. tail[..tailLength]]), next) == 0)
            tailLength = tail.Length;
        return JoinWords([.. owned, .. tail[..tailLength]]);
    }

    // Each chunk's own timings are ordered, but the two chunks' clocks drift, so a word
    // after a seam can start a little before the word kept ahead of it. Shifting it to
    // its predecessor's start keeps the transcript's timings instead of discarding them all.
    private static VocabularyTokenTiming[] KeepStartsInOrder(IEnumerable<VocabularyTokenTiming> timings)
    {
        var ordered = new List<VocabularyTokenTiming>();
        foreach (var timing in timings)
        {
            var start = ordered.Count == 0
                ? timing.StartSeconds
                : Math.Max(timing.StartSeconds, ordered[^1].StartSeconds);
            var shift = start - timing.StartSeconds;
            ordered.Add(
                shift <= 0
                    ? timing
                    : timing with { StartSeconds = start, EndSeconds = timing.EndSeconds + shift }
            );
        }

        return [.. ordered];
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

        return string.Join(
            ' ',
            SplitTokens(accumulated).Concat(SplitTokens(next).Skip(CountTokenOverlap(accumulated, next)))
        );
    }

    private static int CountTokenOverlap(string accumulated, string next)
    {
        var accumulatedTokens = SplitTokens(accumulated);
        var nextTokens = SplitTokens(next);
        var maximumOverlap = Math.Min(
            MaximumFallbackOverlapWords,
            Math.Min(accumulatedTokens.Length, nextTokens.Length)
        );
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
                return length;
        }

        return 0;
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
        // False when the last token's end was only inferred to the chunk's end.
        internal bool EndKnown { get; } = tokens[^1].AcousticEnd > tokens[^1].Timing.StartSeconds;
        internal double KnownEnd => EndKnown ? AcousticEnd : tokens[^1].Timing.StartSeconds + EdgeFrameSeconds;
        internal double Midpoint => (Start + KnownEnd) / 2;
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
