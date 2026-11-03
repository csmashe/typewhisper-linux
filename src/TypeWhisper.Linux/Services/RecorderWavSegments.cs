using System.Buffers.Binary;

namespace TypeWhisper.Linux.Services;

internal static class RecorderWavSegments
{
    internal const int SampleRate = 16000;
    internal const int MaximumSamples = SampleRate * 60 * 60;

    internal static int SampleCount(byte[] wav)
    {
        var bytes = wav.AsSpan();
        if (bytes.Length < 44 || (bytes.Length - 44) % 2 != 0
            || !bytes[..4].SequenceEqual("RIFF"u8)
            || !bytes.Slice(8, 8).SequenceEqual("WAVEfmt "u8)
            || !bytes.Slice(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != bytes.Length - 8
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != 16
            || BinaryPrimitives.ReadInt16LittleEndian(bytes[20..]) != 1
            || BinaryPrimitives.ReadInt16LittleEndian(bytes[22..]) != 1
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]) != SampleRate
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]) != SampleRate * 2
            || BinaryPrimitives.ReadInt16LittleEndian(bytes[32..]) != 2
            || BinaryPrimitives.ReadInt16LittleEndian(bytes[34..]) != 16
            || BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]) != bytes.Length - 44)
        {
            throw new InvalidDataException("Expected canonical 16000 Hz PCM16 mono WAV.");
        }

        return (bytes.Length - 44) / 2;
    }

    internal static byte[] Combine(IReadOnlyList<byte[]> segments)
    {
        var samples = 0;
        // ReSharper disable once LoopCanBeConvertedToQuery -- each step clamps against the running total.
        foreach (var segment in segments)
        {
            samples += Math.Min(SampleCount(segment), MaximumSamples - samples);
        }

        if (samples == 0)
        {
            return [];
        }

        if (segments.Count == 1 && SampleCount(segments[0]) <= MaximumSamples)
        {
            return segments[0];
        }

        var result = new byte[44 + samples * 2];
        segments[0].AsSpan(0, 44).CopyTo(result);
        WriteSizes(result);
        var offset = 44;
        foreach (var segment in segments)
        {
            var count = Math.Min(segment.Length - 44, result.Length - offset);
            segment.AsSpan(44, count).CopyTo(result.AsSpan(offset));
            offset += count;
        }

        return result;
    }

    internal static byte[] Clamp(byte[] wav, int maximumSamples)
    {
        if (SampleCount(wav) <= maximumSamples)
        {
            return wav;
        }

        var result = wav.AsSpan(0, 44 + maximumSamples * 2).ToArray();
        WriteSizes(result);
        return result;
    }

    private static void WriteSizes(byte[] wav)
    {
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), wav.Length - 44);
    }
}
