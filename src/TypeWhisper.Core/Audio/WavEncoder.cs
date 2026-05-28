using System.Buffers.Binary;

namespace TypeWhisper.Core.Audio;

/// <summary>
///     Builds a 44-byte RIFF/WAVE header plus little-endian Int16 PCM data
///     from a float sample buffer. 16-bit only on purpose: the inner loop
///     hardcodes Int16 conversion and most transcription APIs expect PCM16.
/// </summary>
public static class WavEncoder
{
    public static byte[] Encode(
        float[] samples,
        int sampleRate = 16000,
        int channels = 1,
        int bitsPerSample = 16
    )
    {
        // The data-write loop below hardcodes Int16 PCM conversion, so accepting
        // other bit depths here would silently produce a malformed WAV.
        if (bitsPerSample != 16)
        {
            throw new ArgumentException(
                "Only 16-bit PCM is supported.",
                nameof(bitsPerSample)
            );
        }

        // Validate before any header writes so an invalid sampleRate doesn't
        // land in the WAV as a wrap-around value, and an out-of-range
        // channels can't silently truncate when cast to (short).
        if (sampleRate <= 0 || sampleRate > 192000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "sampleRate must be between 1 and 192000 Hz."
            );
        }

        if (channels is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels),
                channels,
                $"channels must be between 1 and {short.MaxValue}."
            );
        }

        var bytesPerSample = bitsPerSample / 8;

        // Compute sizes in long to avoid silent Int32 wrap on extreme inputs
        // (e.g. ~1B+ samples, or sampleRate*channels*bytesPerSample which can
        // exceed Int32 at max channels). The RIFF header stores chunk sizes as
        // little-endian Int32, so anything past Int32.MaxValue is unrepresentable
        // — fail fast with a clear message instead of writing a corrupt header.
        var dataLengthLong = (long)samples.Length * bytesPerSample;
        var totalSizeLong = 44L + dataLengthLong;
        var riffSizeLong = 36L + dataLengthLong;
        var byteRateLong = (long)sampleRate * channels * bytesPerSample;

        if (dataLengthLong < 0 || totalSizeLong > int.MaxValue || byteRateLong > int.MaxValue)
        {
            throw new ArgumentException(
                $"Sample buffer too large for a 32-bit RIFF/WAVE header (samples={samples.Length}, channels={channels}, sampleRate={sampleRate}).",
                nameof(samples)
            );
        }

        var dataLength = (int)dataLengthLong;
        var buffer = new byte[(int)totalSizeLong];

        // RIFF/WAVE header (44 bytes total, PCM format type 1)
        "RIFF"u8.CopyTo(buffer.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), (int)riffSizeLong);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));

        // fmt sub-chunk
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16); // chunk size (always 16 for PCM)
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1); // audio format: 1 = PCM
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), (int)byteRateLong); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(
            buffer.AsSpan(32),
            (short)(channels * bytesPerSample)
        ); // block align
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), (short)bitsPerSample);

        // data sub-chunk
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataLength);

        // Convert normalized float [-1, 1] to signed Int16 PCM; clamp to avoid wrap-around on values outside range
        var dataSpan = buffer.AsSpan(44);
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var pcm = (short)(clamped * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(dataSpan[(i * 2)..], pcm);
        }

        return buffer;
    }
}