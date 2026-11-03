using System.Buffers.Binary;

namespace TypeWhisper.Linux.Services.Vocabulary;

internal static class PcmWav
{
    internal static float[] ToMonoSamples16K(byte[] wav)
    {
        if (
            wav.Length < 12
            || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)
        )
            return [];

        var validFormat = false;
        var dataOffset = 0;
        var dataLength = 0;
        for (long offset = 12; offset <= wav.Length - 8; )
        {
            var header = wav.AsSpan((int)offset, 8);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var start = (int)offset + 8;
            var remaining = wav.Length - start;
            if (header[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || size > remaining)
                    return [];
                var format = wav.AsSpan(start, 16);
                validFormat =
                    BinaryPrimitives.ReadUInt16LittleEndian(format) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) == 16000
                    && BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) == 16;
                if (!validFormat)
                    return [];
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                dataOffset = start;
                dataLength = (int)Math.Min(size, (uint)remaining);
            }
            else if (size > remaining)
                return [];

            offset = start + size + (size & 1);
        }

        if (!validFormat || dataLength == 0)
            return [];
        var samples = new float[dataLength / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] =
                BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(dataOffset + i * 2, 2)) / 32768f;
        return samples;
    }
}
