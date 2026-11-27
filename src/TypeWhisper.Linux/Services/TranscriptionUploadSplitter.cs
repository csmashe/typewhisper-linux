using System.Buffers.Binary;
using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Sends a batch upload whole, and only when the provider rejects it as too large (HTTP 413 or
///     <see cref="PluginRequestFailureKind.RequestTooLarge" />) splits the PCM16 audio into halves,
///     recursively, and joins their results. Chunks are contiguous: no audio is dropped or repeated,
///     and timestamps keep each chunk's offset. A failed chunk fails the whole call; partial text is
///     never returned.
/// </summary>
internal static class TranscriptionUploadSplitter
{
    private const int MinimumSplitSeconds = 2;
    private const int MaximumDepth = 20;

    internal static async Task<PluginTranscriptionResult> TranscribeAsync(
        byte[] wav,
        Func<byte[], CancellationToken, Task<PluginTranscriptionResult>> send,
        CancellationToken ct
    )
    {
        try
        {
            return await send(wav, ct);
        }
        catch (PluginRequestException ex) when (
            IsTooLarge(ex)
            && !ct.IsCancellationRequested
            && Pcm16Wav.TryParse(wav, out var audio)
            && CanSplit(audio, audio.FrameCount)
        )
        {
            Trace.WriteLine(
                $"[TranscriptionSplit] Provider rejected {wav.Length} bytes as too large; splitting "
                    + $"{audio.FrameCount / (double)audio.SampleRate:F1} s of audio."
            );
            var parts = new List<(int Offset, PluginTranscriptionResult Result)>();
            await SplitAsync(audio, 0, audio.FrameCount, 1, parts, send, ct);
            return Merge(audio, parts);
        }
    }

    private static async Task SendPartAsync(
        Pcm16Wav audio,
        int offset,
        int count,
        int depth,
        List<(int Offset, PluginTranscriptionResult Result)> parts,
        Func<byte[], CancellationToken, Task<PluginTranscriptionResult>> send,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            parts.Add((offset, await send(audio.Encode(offset, count), ct)));
        }
        catch (PluginRequestException ex) when (
            IsTooLarge(ex) && !ct.IsCancellationRequested && depth < MaximumDepth && CanSplit(audio, count)
        )
        {
            await SplitAsync(audio, offset, count, depth + 1, parts, send, ct);
        }
    }

    private static async Task SplitAsync(
        Pcm16Wav audio,
        int offset,
        int count,
        int depth,
        List<(int Offset, PluginTranscriptionResult Result)> parts,
        Func<byte[], CancellationToken, Task<PluginTranscriptionResult>> send,
        CancellationToken ct
    )
    {
        var left = audio.FindQuietBoundary(offset, count / 2);
        await SendPartAsync(audio, offset, left, depth, parts, send, ct);
        await SendPartAsync(audio, offset + left, count - left, depth, parts, send, ct);
    }

    private static bool IsTooLarge(PluginRequestException failure) =>
        failure.FailureKind == PluginRequestFailureKind.RequestTooLarge || failure.HttpStatusCode == 413;

    // Only two seconds or more is split, so no chunk gets shorter than a second.
    private static bool CanSplit(Pcm16Wav audio, int frames) => frames >= audio.SampleRate * MinimumSplitSeconds;

    private static PluginTranscriptionResult Merge(
        Pcm16Wav audio,
        List<(int Offset, PluginTranscriptionResult Result)> parts
    )
    {
        // A language is reported only when every chunk that detected one agrees on it.
        var languages = parts
            .Select(part => part.Result.DetectedLanguage)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PluginTranscriptionResult(
            string.Join(" ", parts.Select(part => part.Result.Text.Trim()).Where(text => text.Length > 0)),
            languages.Length == 1 ? languages[0] : null,
            audio.FrameCount / (double)audio.SampleRate,
            parts.All(part => part.Result.NoSpeechProbability.HasValue)
                ? parts.Min(part => part.Result.NoSpeechProbability)
                : null
        )
        {
            Segments = parts
                .SelectMany(part =>
                {
                    var shift = part.Offset / (double)audio.SampleRate;
                    return part.Result.Segments.Select(segment =>
                        segment with { Start = segment.Start + shift, End = segment.End + shift });
                })
                .ToArray(),
            TokenTimings = parts
                .SelectMany(part =>
                {
                    var shift = part.Offset / (double)audio.SampleRate;
                    return part.Result.TokenTimings.Select(token => token with
                    {
                        StartSeconds = token.StartSeconds + shift,
                        EndSeconds = token.EndSeconds + shift,
                    });
                })
                .ToArray(),
        };
    }

    /// <summary>The PCM16 data of a RIFF/WAVE upload, sliceable into standalone WAVs by frame.</summary>
    internal readonly record struct Pcm16Wav(ReadOnlyMemory<byte> Data, int Channels, int SampleRate)
    {
        private const int HeaderBytes = 44;

        private int BlockAlign => Channels * 2;

        internal int FrameCount => Data.Length / BlockAlign;

        /// <summary>Reads 16-bit PCM WAVs, including ones with extra chunks (for example ffmpeg's LIST).</summary>
        internal static bool TryParse(byte[] wav, out Pcm16Wav audio)
        {
            audio = default;
            if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                return false;
            }

            int? channels = null;
            int? sampleRate = null;
            var position = 12;
            while (position + 8 <= wav.Length)
            {
                var id = wav.AsSpan(position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4, 4));
                var body = position + 8;
                var available = wav.Length - body;
                if (id.SequenceEqual("fmt "u8))
                {
                    if (size < 16 || available < 16)
                    {
                        return false;
                    }

                    var format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body, 2));
                    var bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 14, 2));
                    if (format != 1 || bits != 16)
                    {
                        return false;
                    }

                    channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(body + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4, 4));
                }
                else if (id.SequenceEqual("data"u8))
                {
                    if (channels is not > 0 || sampleRate is not > 0)
                    {
                        return false;
                    }

                    // A streamed WAV may carry a placeholder size; the bytes actually present win.
                    var length = (int)Math.Min(size, (uint)available);
                    length -= length % (channels.Value * 2);
                    audio = new Pcm16Wav(wav.AsMemory(body, length), channels.Value, sampleRate.Value);
                    return true;
                }

                // Chunks are padded to an even size.
                var next = body + size + (size & 1);
                if (next > wav.Length)
                {
                    return false;
                }

                position = (int)next;
            }

            return false;
        }

        internal byte[] Encode(int offset, int count)
        {
            var data = Data.Span.Slice(offset * BlockAlign, count * BlockAlign);
            var wav = new byte[HeaderBytes + data.Length];
            var header = wav.AsSpan();
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + data.Length);
            "WAVE"u8.CopyTo(header[8..]);
            "fmt "u8.CopyTo(header[12..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
            BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)Channels);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * BlockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)BlockAlign);
            BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
            "data"u8.CopyTo(header[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(header[40..], data.Length);
            data.CopyTo(header[HeaderBytes..]);
            return wav;
        }

        /// <summary>
        ///     A split point near <paramref name="limit" /> frames past <paramref name="offset" />, moved to the
        ///     quietest 20 ms window in the second before it when one is below speech level (RMS 0.01).
        /// </summary>
        internal int FindQuietBoundary(int offset, int limit)
        {
            var window = SampleRate / 50;
            if (window == 0)
            {
                return Math.Max(1, limit);
            }

            var boundary = limit;
            var bestEnergy = 0.0001;
            var samples = Data.Span;
            // A quiet point (end - window / 2) counts only when it leaves at least a second on the left.
            for (
                var end = limit;
                end - window >= Math.Max(limit / 2, limit - SampleRate) && end - window / 2 >= SampleRate;
                end -= window
            )
            {
                double energy = 0;
                var start = (offset + end - window) * BlockAlign;
                for (var i = 0; i < window * BlockAlign; i += 2)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(samples[(start + i)..]) / 32768.0;
                    energy += sample * sample;
                }

                energy /= window * Channels;
                if (energy >= bestEnergy)
                {
                    continue;
                }

                bestEnergy = energy;
                boundary = end - window / 2;
            }

            return Math.Max(1, boundary);
        }
    }
}
