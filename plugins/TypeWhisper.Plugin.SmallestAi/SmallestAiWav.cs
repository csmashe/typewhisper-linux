using System.Buffers.Binary;

namespace TypeWhisper.Plugin.SmallestAi;

internal sealed record SmallestAiPcmAudio(ReadOnlyMemory<byte> Samples, int SampleRate, int Channels)
{
    public TimeSpan Duration =>
        TimeSpan.FromSeconds((double)Samples.Length / (Channels * sizeof(short)) / SampleRate);
}

/// <summary>Reads synthesis WAV output, so playback uses the rate and channels the audio declares.</summary>
internal static class SmallestAiWav
{
    private const ushort PcmFormat = 1;
    private const ushort ExtensibleFormat = 0xFFFE;
    private const int BitsPerSample = 16;

    /// <summary>Returns null for anything that is not non-empty 16-bit PCM.</summary>
    public static SmallestAiPcmAudio? Parse(byte[] wav)
    {
        var span = wav.AsSpan();
        if (span.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span.Slice(8, 4).SequenceEqual("WAVE"u8))
            return null;

        (int Rate, int Channels)? format = null;
        var offset = 12;
        while (offset + 8 <= span.Length)
        {
            var id = span.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
            var body = offset + 8;
            var available = span.Length - body;

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || available < 16)
                    return null;

                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body, 2));
                int channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 2, 2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(body + 4, 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(body + 14, 2));
                if (encoding is not (PcmFormat or ExtensibleFormat) || bits != BitsPerSample
                    || channels is < 1 or > 2 || rate is < 8_000 or > 192_000)
                {
                    return null;
                }

                format = (rate, channels);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is not var (rate, channels))
                    return null;

                // Streamed WAVs can declare an unknown (oversized) length; keep the whole frames present.
                var frameSize = channels * (BitsPerSample / 8);
                var length = (int)Math.Min(size, (uint)available);
                length -= length % frameSize;
                return length == 0 ? null : new SmallestAiPcmAudio(wav.AsMemory(body, length), rate, channels);
            }

            // Chunks are word-aligned: an odd-sized chunk is followed by one pad byte.
            var next = body + size + (size & 1);
            if (next > span.Length)
                return null;
            offset = (int)next;
        }

        return null;
    }
}
