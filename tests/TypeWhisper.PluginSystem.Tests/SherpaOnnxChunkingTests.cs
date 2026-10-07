extern alias SherpaOnnx;

using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxChunkingTests
{
    private const int SampleRate = SherpaDecodeCoordinator.SampleRate;

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(61, false)]
    [InlineData(61, true)]
    [InlineData(300, false)]
    public void PlanChunks_OwnedRangesCoverEverySampleWithinTheMaximum(
        int seconds,
        bool canary
    )
    {
        var audio = Speech(seconds * SampleRate + 123);

        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, canary);

        Assert.Equal(0, chunks[0].OwnedStart);
        Assert.Equal(audio.Length, chunks[^1].OwnedEnd);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.InRange(chunk.End - chunk.Start, 1, SherpaDecodeCoordinator.MaximumChunkSampleCount);
            Assert.True(chunk.OwnedStart < chunk.OwnedEnd);
            Assert.InRange(chunk.OwnedStart, chunk.Start, chunk.End);
            Assert.InRange(chunk.OwnedEnd, chunk.Start, chunk.End);
            if (i > 0)
                Assert.Equal(chunks[i - 1].OwnedEnd, chunk.OwnedStart);

            var context = canary || chunks.Count == 1
                ? 0
                : SherpaDecodeCoordinator.BoundaryContextSampleCount;
            if (i > 0)
                Assert.Equal(context, chunk.OwnedStart - chunk.Start);
            if (i < chunks.Count - 1)
                Assert.Equal(context, chunk.End - chunk.OwnedEnd);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanChunks_CutsInThePauseRatherThanThroughSpeech(bool canary)
    {
        var audio = Speech(40 * SampleRate);
        // A 200 ms pause inside the boundary search window of the first cut.
        const int pauseStart = 13 * SampleRate;
        Array.Clear(audio, pauseStart, SampleRate / 5);

        var cut = SherpaDecodeCoordinator.PlanChunks(audio, canary)[0].OwnedEnd;

        Assert.InRange(cut, pauseStart, pauseStart + SampleRate / 5);
    }

    [Fact]
    public void Canary_KeepsAWordGenuinelyRepeatedAcrossACut()
    {
        var payloads = new Queue<string>(
            [
                """{"text":"I said that","lang":"en"}""",
                """{"text":"that is fine.","lang":"en"}""",
            ]
        );
        var samples = new List<int>();
        var coordinator = new SherpaDecodeCoordinator(chunk =>
        {
            samples.Add(chunk.Length);
            return new SherpaDecodeChunk(payloads.Dequeue());
        });
        var audio = Speech(SherpaDecodeCoordinator.MaximumChunkSampleCount + SampleRate);

        var result = coordinator.Decode(audio, parseCanaryPayload: true, CancellationToken.None);

        Assert.Equal("I said that that is fine.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Empty(result.TokenTimings);
        // Non-overlapping chunks decode each sample exactly once.
        Assert.Equal(audio.Length, samples.Sum());
    }

    [Fact]
    public void Canary_MinuteLongRecording_KeepsEverySectionAndSkipsEmptyChunks()
    {
        var audio = Speech(61 * SampleRate);
        var chunkCount = SherpaDecodeCoordinator.PlanChunks(audio, true).Count;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
        {
            var i = index++;
            return new SherpaDecodeChunk(
                i == 1 ? """{"text":"","lang":"en"}""" : $$"""{"text":"section {{i}}","lang":"de"}"""
            );
        });

        var result = coordinator.Decode(audio, parseCanaryPayload: true, CancellationToken.None);

        Assert.True(chunkCount >= 5);
        Assert.Equal(chunkCount, index);
        var expected = string.Join(
            ' ',
            Enumerable.Range(0, chunkCount).Where(i => i != 1).Select(i => $"section {i}")
        );
        Assert.Equal(expected, result.Text);
        Assert.Equal("de", result.DetectedLanguage);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.08)]
    [InlineData(-0.08)]
    [InlineData(0.15)]
    [InlineData(-0.15)]
    public void Transducer_BoundaryWordIsKeptOnceWithAbsoluteTimings(double laterChunkJitter)
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        // "fox" starts exactly on the cut; the later chunk times it with jitter.
        var words = new[]
        {
            new Word("the", 1.0),
            new Word("quick", cut - 1.0),
            new Word("brown", cut - 0.5),
            new Word("fox", cut),
            new Word("jumps", cut + 0.4),
            new Word("high.", cut + 2.0),
        };

        var result = DecodeTransducer(audio, words, chunkIndex => chunkIndex == 0 ? 0 : laterChunkJitter);

        Assert.Equal("the quick brown fox jumps high.", result.Text);
        AssertTimingsValid(result, audio);
        Assert.Equal(words.Length, result.TokenTimings.Count);
        Assert.Equal(1.0, result.TokenTimings[0].StartSeconds, 6);
        Assert.Equal(cut + 2.0 + laterChunkJitter, result.TokenTimings[^1].StartSeconds, 4);
    }

    [Fact]
    public void Transducer_KeepsAWordGenuinelyRepeatedAcrossACut()
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        var words = new[]
        {
            new Word("I", cut - 1.2),
            new Word("said", cut - 0.8),
            new Word("that", cut - 0.3),
            new Word("that", cut + 0.05),
            new Word("twice", cut + 0.4),
        };

        var result = DecodeTransducer(audio, words, _ => 0);

        Assert.Equal("I said that that twice", result.Text);
        Assert.Equal(words.Length, result.TokenTimings.Count);
        AssertTimingsValid(result, audio);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.08)]
    [InlineData(-0.08)]
    public void Transducer_KeepsCloseRepetitionsOnTheCutOneToOne(double laterChunkJitter)
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        // Both chunks hear both occurrences; one kept "no" must not cancel two.
        var words = new[]
        {
            new Word("no", cut - 0.1, Duration: 0.1),
            new Word("no", cut + 0.02, Duration: 0.1),
            new Word("thanks", cut + 0.3),
        };

        var result = DecodeTransducer(audio, words, chunkIndex => chunkIndex == 0 ? 0 : laterChunkJitter);

        Assert.Equal("no no thanks", result.Text);
        AssertTimingsValid(result, audio);
    }

    [Theory]
    [InlineData(0.3)]
    // Near the later chunk's first sample, where clipped words are also handled.
    [InlineData(0.4)]
    public void Transducer_KeepsALongWordHeardWholeOnlyByTheLaterChunk(double secondsBeforeCut)
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        // The earlier chunk ends 0.5 s after the cut, before this word does.
        var words = new[]
        {
            new Word("before", cut - 1.0),
            new Word("supercalifragilistic", cut - secondsBeforeCut, Duration: 1.0),
            new Word("after", cut + 1.0),
        };

        var result = DecodeTransducer(audio, words, _ => 0);

        Assert.Equal("before supercalifragilistic after", result.Text);
        Assert.Equal(3, result.TokenTimings.Count);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_MatchesRepeatedBoundaryWordsInOrder()
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        // The later chunk times both kept copies 0.15 s late.
        var words = new[]
        {
            new Word("no", cut - 0.2, Duration: 0.1),
            new Word("no", cut - 0.08, Duration: 0.1),
        };

        var result = DecodeTransducer(audio, words, chunkIndex => chunkIndex == 0 ? 0 : 0.15);

        Assert.Equal("no no", result.Text);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_ReplacesAWordTheEarlierChunkHeardTruncated()
    {
        var audio = Speech(20 * SampleRate);
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var cut = chunks[0].OwnedEnd / (double)SampleRate;
        var laterStart = chunks[1].Start / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                // Runs to the end of the earlier chunk's audio, 0.5 s after the cut.
                ? new SherpaDecodeChunk(
                    "before super",
                    ["▁before", "▁super"],
                    [(float)(cut - 1.0), (float)(cut - 0.3)],
                    [0.25f, 0.8f]
                )
                : new SherpaDecodeChunk(
                    "supercalifragilistic after",
                    ["▁supercalifragilistic", "▁after"],
                    [(float)(cut - 0.3 - laterStart), (float)(cut + 1.0 - laterStart)],
                    [1.0f, 0.25f]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("before supercalifragilistic after", result.Text);
        Assert.Equal(3, result.TokenTimings.Count);
        AssertTimingsValid(result, audio);
    }

    [Theory]
    [InlineData(0.0)]
    // Past the clip allowance, but still overlapping the kept copy of the same word.
    [InlineData(0.18)]
    public void Transducer_DropsAWordClippedAtTheLaterChunksLeadingEdge(double clippedStartOffset)
    {
        var audio = Speech(20 * SampleRate);
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var cut = chunks[0].OwnedEnd / (double)SampleRate;
        var laterStart = chunks[1].Start / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk(
                    "quick brown fox",
                    ["▁quick", "▁brown", "▁fox"],
                    [(float)(cut - 1.2), (float)(laterStart - 0.2), (float)(cut + 0.1)],
                    [0.25f, 0.45f, 0.25f]
                )
                // The later chunk begins inside "brown" and hears only its tail.
                : new SherpaDecodeChunk(
                    "brown fox jumps",
                    ["▁brown", "▁fox", "▁jumps"],
                    [(float)clippedStartOffset, (float)(cut + 0.1 - laterStart), (float)(cut + 1.0 - laterStart)],
                    [0.05f, 0.25f, 0.25f]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("quick brown fox jumps", result.Text);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_ClippedCopyCancelsOnlyOneLaterRepetition()
    {
        var audio = Speech(20 * SampleRate);
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var cut = chunks[0].OwnedEnd / (double)SampleRate;
        var laterStart = chunks[1].Start / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk("no", ["▁no"], [(float)(cut - 0.7)], [0.7f])
                // A clipped copy of the kept "no", then a genuine repetition.
                : new SherpaDecodeChunk(
                    "no no",
                    ["▁no", "▁no"],
                    [0f, (float)(cut - 0.08 - laterStart)],
                    [0.42f, 0.25f]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("no no", result.Text);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_ZeroTerminalDurationDoesNotReplaceAnEarlierWord()
    {
        var audio = Speech(20 * SampleRate);
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var cut = chunks[0].OwnedEnd / (double)SampleRate;
        var laterStart = chunks[1].Start / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                // A zero duration stretches "before" to the chunk's end.
                ? new SherpaDecodeChunk("before", ["▁before"], [(float)(cut - 1.0)], [0f])
                : new SherpaDecodeChunk(
                    "supercalifragilistic",
                    ["▁supercalifragilistic"],
                    [(float)(cut - 0.3 - laterStart)],
                    [1.0f]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("before supercalifragilistic", result.Text);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_MinuteLongRecording_KeepsEveryWordOnceWithSubwordTokens()
    {
        var audio = Speech(61 * SampleRate);
        // A two-piece word every 0.4 s, so words straddle every cut and context edge.
        var words = Enumerable
            .Range(0, 150)
            .Select(i => new Word($"w{i}", 0.2 + i * 0.4, Pieces: 2))
            .ToArray();

        var result = DecodeTransducer(audio, words, chunkIndex => chunkIndex % 2 == 0 ? 0.07 : -0.07);

        Assert.Equal(string.Join(' ', words.Select(word => word.Text)), result.Text);
        Assert.Equal(words.Length * 2, result.TokenTimings.Count);
        AssertTimingsValid(result, audio);
    }

    [Fact]
    public void Transducer_MalformedChunkTimings_KeepsTextAndPublishesNoTimings()
    {
        var audio = Speech(20 * SampleRate);
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk(
                    "the quick brown fox",
                    ["▁the", "▁quick", "▁brown", "▁fox"],
                    [0.1f, 0.2f, 0.3f, 0.4f]
                )
                : new SherpaDecodeChunk(
                    "Fox jumps high",
                    ["▁Fox", "▁jumps", "▁high"],
                    [0.3f, float.NaN, 0.5f]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("the quick brown fox jumps high", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void Transducer_MissingDurations_FallBackToText()
    {
        var audio = Speech(20 * SampleRate);
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var cut = chunks[0].OwnedEnd / (double)SampleRate;
        var laterStart = chunks[1].Start / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk("hello", ["▁hello"], [(float)(cut - 0.7)])
                : new SherpaDecodeChunk(
                    "hello world",
                    ["▁hello", "▁world"],
                    [0f, (float)(cut + 0.5 - laterStart)]
                )
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Theory]
    [InlineData("jumps high")]
    // Restates an owned word but still misses "fox".
    [InlineData("quick jumps high")]
    public void Transducer_FallbackKeepsTheTimedChunksTrailingContextWords(string untimedText)
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                // "fox" starts after the cut, in the timed chunk's trailing context.
                ? new SherpaDecodeChunk(
                    "the quick fox",
                    ["▁the", "▁quick", "▁fox"],
                    [(float)(cut - 1.0), (float)(cut - 0.5), (float)(cut + 0.1)],
                    [0.25f, 0.25f, 0.2f]
                )
                // Untimed, and it missed "fox" at its leading edge.
                : new SherpaDecodeChunk(untimedText)
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal(
            untimedText.StartsWith("quick", StringComparison.Ordinal)
                ? "the quick jumps high"
                : "the quick fox jumps high",
            result.Text
        );
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void Transducer_FallbackSeamsOnAPartOfTheTrailingContext()
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                // "can go" is heard whole after the cut.
                ? new SherpaDecodeChunk(
                    "we can go",
                    ["▁we", "▁can", "▁go"],
                    [(float)(cut - 0.5), (float)(cut + 0.05), (float)(cut + 0.2)],
                    [0.2f, 0.1f, 0.1f]
                )
                : new SherpaDecodeChunk("can continue")
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("we can continue", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Theory]
    [InlineData(0.4f)]
    // Unknown length: it cannot be shown to be complete.
    [InlineData(0f)]
    public void Transducer_FallbackDropsATrailingWordTheTimedChunkHeardTruncated(float superDuration)
    {
        var audio = Speech(20 * SampleRate);
        var cut = SherpaDecodeCoordinator.PlanChunks(audio, false)[0].OwnedEnd / (double)SampleRate;
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                // "super" runs to the end of the chunk's audio, 0.5 s after the cut.
                ? new SherpaDecodeChunk(
                    "before super",
                    ["▁before", "▁super"],
                    [(float)(cut - 1.0), (float)(cut + 0.1)],
                    [0.25f, superDuration]
                )
                : new SherpaDecodeChunk("supercalifragilistic after")
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("before supercalifragilistic after", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void Transducer_TokensThatDoNotSpellTheText_FallBackToText()
    {
        var audio = Speech(20 * SampleRate);
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk("hello there", ["▁hello", "▁world"], [0.1f, 0.5f])
                : new SherpaDecodeChunk("general", ["▁general"], [1.0f])
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("hello there general", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void Transducer_UntimedFallback_BoundsOverlapRemovalToTheSharedAudio()
    {
        var audio = Speech(20 * SampleRate);
        var index = 0;
        // Without timings, a long repeated phrase is not the 1 s of shared audio.
        var coordinator = new SherpaDecodeCoordinator(_ =>
            index++ == 0
                ? new SherpaDecodeChunk("one two three four five six seven")
                : new SherpaDecodeChunk("one two three four five six seven eight")
        );

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal(
            "one two three four five six seven one two three four five six seven eight",
            result.Text
        );
    }

    [Fact]
    public void Transducer_SilentLaterChunk_KeepsEarlierTimings()
    {
        var audio = Speech(20 * SampleRate);
        var words = new[] { new Word("hello", 1.0), new Word("world", 2.0) };

        var result = DecodeTransducer(audio, words, _ => 0);

        Assert.Equal("hello world", result.Text);
        Assert.Equal(2, result.TokenTimings.Count);
        AssertTimingsValid(result, audio);
    }

    private static SherpaDecodeResult DecodeTransducer(
        float[] audio,
        Word[] words,
        Func<int, double> jitterForChunk
    )
    {
        var chunks = SherpaDecodeCoordinator.PlanChunks(audio, false);
        var index = 0;
        var coordinator = new SherpaDecodeCoordinator(samples =>
        {
            var chunkIndex = index++;
            var chunk = chunks[chunkIndex];
            Assert.Equal(chunk.End - chunk.Start, samples.Length);
            var start = chunk.Start / (double)SampleRate;
            var end = chunk.End / (double)SampleRate;
            var jitter = jitterForChunk(chunkIndex);
            // The decoder hears a word only when the chunk holds all of it.
            var heard = words
                .Where(word => word.Start >= start && word.Start + word.Duration <= end)
                .ToArray();
            var tokens = new List<string>();
            var starts = new List<float>();
            var durations = new List<float>();
            foreach (var word in heard)
            {
                var pieceDuration = word.Duration / word.Pieces;
                for (var piece = 0; piece < word.Pieces; piece++)
                {
                    var pieceLength = (word.Text.Length + word.Pieces - 1) / word.Pieces;
                    var text = word.Text.Substring(
                        Math.Min(word.Text.Length, piece * pieceLength),
                        Math.Min(pieceLength, Math.Max(0, word.Text.Length - piece * pieceLength))
                    );
                    tokens.Add(piece == 0 ? "▁" + text : text);
                    starts.Add((float)Math.Max(0, word.Start - start + jitter + piece * pieceDuration));
                    durations.Add((float)pieceDuration);
                }
            }

            return new SherpaDecodeChunk(
                string.Join(' ', heard.Select(word => word.Text)),
                [.. tokens],
                [.. starts],
                [.. durations]
            );
        });

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);
        Assert.Equal(chunks.Count, index);
        return result;
    }

    private static void AssertTimingsValid(SherpaDecodeResult result, float[] audio)
    {
        var duration = audio.Length / (double)SampleRate;
        VocabularyTokenTiming? previous = null;
        foreach (var timing in result.TokenTimings)
        {
            Assert.InRange(timing.StartSeconds, 0, duration);
            Assert.True(timing.EndSeconds > timing.StartSeconds);
            Assert.True(timing.EndSeconds <= duration);
            if (previous is not null)
                Assert.True(timing.StartSeconds >= previous.StartSeconds);
            previous = timing;
        }

        // The rescorer aligns token text against the transcript in order.
        var cursor = 0;
        foreach (var timing in result.TokenTimings)
        {
            var token = timing.Text.Replace('▁', ' ').Trim();
            var position = result.Text.IndexOf(token, cursor, StringComparison.Ordinal);
            Assert.True(position >= 0, $"token '{token}' not found after {cursor}");
            cursor = position + token.Length;
        }
    }

    // Low-level noise: no digital silence, so cuts are chosen by energy alone.
    private static float[] Speech(int sampleCount)
    {
        var random = new Random(1234);
        var audio = new float[sampleCount];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = (float)(random.NextDouble() * 0.2 - 0.1) + 0.3f;
        return audio;
    }

    private sealed record Word(string Text, double Start, int Pieces = 1, double Duration = 0.25);
}
