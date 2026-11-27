using System.Buffers.Binary;
using TypeWhisper.Core.Audio;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TranscriptionUploadSplitterTests
{
    private const int SampleRate = 16_000;

    [Fact]
    public async Task AcceptedUpload_IsSentOnceUnchanged()
    {
        var wav = WavEncoder.Encode(Tone(SampleRate * 4));
        var sent = new List<byte[]>();

        var result = await TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            sent.Add(upload);
            return Task.FromResult(new PluginTranscriptionResult("whole", "en", 4));
        }, CancellationToken.None);

        Assert.Equal("whole", result.Text);
        Assert.Same(wav, Assert.Single(sent));
    }

    [Theory]
    [InlineData(PluginRequestFailureKind.RequestTooLarge, null)]
    [InlineData(PluginRequestFailureKind.InvalidRequest, 413)]
    public async Task TooLarge_SplitsRecursivelyIntoContiguousChunksUnderTheLimit(
        PluginRequestFailureKind kind, int? status)
    {
        var samples = Tone(SampleRate * 10);
        var wav = WavEncoder.Encode(samples);
        var limit = wav.Length / 3;
        var accepted = new List<byte[]>();

        var result = await TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            if (upload.Length > limit)
                throw new PluginRequestException("too large", kind, status);
            accepted.Add(upload);
            return Task.FromResult(new PluginTranscriptionResult($" part{accepted.Count} ", "en", 0));
        }, CancellationToken.None);

        Assert.Equal(4, accepted.Count);
        Assert.All(accepted, upload => Assert.True(upload.Length <= limit));
        Assert.Equal(wav[44..], accepted.SelectMany(upload => upload[44..]).ToArray());
        Assert.Equal("part1 part2 part3 part4", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(10, result.DurationSeconds);
    }

    [Fact]
    public async Task SplitResult_ShiftsTimestampsAndReportsLanguageOnlyWhenChunksAgree()
    {
        var wav = WavEncoder.Encode(Tone(SampleRate * 4));
        var calls = 0;

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the fake rejects the whole upload by its size; that check is what the test sets up.
        var result = await TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            if (upload.Length == wav.Length)
                throw new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);
            calls++;
            return Task.FromResult(new PluginTranscriptionResult($"t{calls}", calls == 1 ? "en" : "de", 0, 0.1f * calls)
            {
                Segments = [new PluginTranscriptionSegment($"t{calls}", 0.5, 1.0)],
                TokenTimings = [new VocabularyTokenTiming($"t{calls}", 0.25, 0.75)],
            });
        }, CancellationToken.None);

        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0.1f, result.NoSpeechProbability);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0.5, result.Segments[0].Start);
        Assert.Equal(2.5, result.Segments[1].Start, 3);
        Assert.Equal(3.0, result.Segments[1].End, 3);
        Assert.Equal(2.25, result.TokenTimings[1].StartSeconds, 3);
    }

    [Fact]
    public async Task SplitPoint_MovesToSilenceBeforeTheMidpoint()
    {
        var samples = Tone(SampleRate * 4);
        // 100 ms of silence ending 300 ms before the midpoint.
        Array.Clear(samples, SampleRate * 2 - 6400, 1600);
        var wav = WavEncoder.Encode(samples);
        var frames = new List<int>();

        await TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            if (upload.Length == wav.Length)
                throw new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);
            frames.Add((upload.Length - 44) / 2);
            return Task.FromResult(new PluginTranscriptionResult("t", null, 0, null));
        }, CancellationToken.None);

        Assert.InRange(frames[0], SampleRate * 2 - 6400, SampleRate * 2 - 4800);
        Assert.Equal(SampleRate * 4, frames.Sum());
    }

    [Fact]
    public async Task SplitPoint_IgnoresSilenceThatWouldLeaveAChunkUnderASecond()
    {
        var samples = Tone(SampleRate * 2);
        // 20 ms of silence half a second in; splitting there would leave a 0.51 s chunk.
        Array.Clear(samples, SampleRate / 2, SampleRate / 50);
        var wav = WavEncoder.Encode(samples);
        var frames = new List<int>();

        await TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            if (upload.Length == wav.Length)
                throw new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);
            frames.Add((upload.Length - 44) / 2);
            return Task.FromResult(new PluginTranscriptionResult("t", null, 0, null));
        }, CancellationToken.None);

        Assert.Equal([SampleRate, SampleRate], frames);
    }

    [Fact]
    public async Task TooLarge_ShortOrUnreadableAudio_Rethrows()
    {
        var failure = new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);

        Assert.Same(failure, await Assert.ThrowsAsync<PluginRequestException>(() =>
            TranscriptionUploadSplitter.TranscribeAsync(WavEncoder.Encode(Tone(SampleRate * 2 - 1)), Reject, CancellationToken.None)));
        Assert.Same(failure, await Assert.ThrowsAsync<PluginRequestException>(() =>
            TranscriptionUploadSplitter.TranscribeAsync(new byte[SampleRate * 8], Reject, CancellationToken.None)));
        return;

        Task<PluginTranscriptionResult> Reject(byte[] upload, CancellationToken _) => throw failure;
    }

    [Fact]
    public async Task TooLarge_StopsSplittingBelowTwoSeconds()
    {
        var wav = WavEncoder.Encode(Tone(SampleRate * 8));
        var calls = 0;

        await Assert.ThrowsAsync<PluginRequestException>(() => TranscriptionUploadSplitter.TranscribeAsync(wav, (_, _) =>
        {
            calls++;
            throw new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);
        }, CancellationToken.None));

        // 8 s, then 4 s, 2 s and finally 1 s, which is too short to split again.
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task FailedLaterChunk_FailsTheWholeCall()
    {
        var wav = WavEncoder.Encode(Tone(SampleRate * 4));
        var calls = 0;

        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the fake rejects the whole upload by its size; that check is what the test sets up.
        await Assert.ThrowsAsync<PluginRequestException>(() => TranscriptionUploadSplitter.TranscribeAsync(wav, (upload, _) =>
        {
            if (upload.Length == wav.Length)
                throw new PluginRequestException("too large", PluginRequestFailureKind.RequestTooLarge);
            return ++calls == 2
                ? throw new PluginRequestException("bad key", PluginRequestFailureKind.Authentication)
                : Task.FromResult(new PluginTranscriptionResult("first", null, 0, null));
        }, CancellationToken.None));
    }

    [Fact]
    public void Parse_ReadsWavWithExtraChunksAndPlaceholderDataSize()
    {
        var samples = Tone(SampleRate);
        var canonical = WavEncoder.Encode(samples);
        // ffmpeg-style: a LIST chunk between fmt and data, and a streamed data size.
        byte[] list = [.. "LIST"u8, 5, 0, 0, 0, 1, 2, 3, 4, 5, 0];
        var wav = canonical[..36].Concat(list).Concat(canonical[36..]).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(36 + list.Length + 4), uint.MaxValue);

        Assert.True(TranscriptionUploadSplitter.Pcm16Wav.TryParse(wav, out var audio));
        Assert.Equal(SampleRate, audio.FrameCount);
        Assert.Equal(canonical, audio.Encode(0, audio.FrameCount));
    }

    private static float[] Tone(int count) =>
        Enumerable.Range(0, count).Select(i => 0.5f * MathF.Sin(i * 0.1f)).ToArray();
}
