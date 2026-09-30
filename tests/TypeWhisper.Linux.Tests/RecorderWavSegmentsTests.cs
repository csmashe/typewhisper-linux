using System.Buffers.Binary;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecorderWavSegmentsTests
{
    [Fact]
    public void Combine_ConcatenatesPayloadAndWritesHeaderSizes()
    {
        var first = Wav(2);
        var second = Wav(3);
        first[44] = 1;
        second[44] = 2;
        var combined = RecorderWavSegments.Combine([first, second]);
        Assert.Equal(5, RecorderWavSegments.SampleCount(combined));
        Assert.Equal(46, BinaryPrimitives.ReadInt32LittleEndian(combined.AsSpan(4)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(combined.AsSpan(40)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(combined.AsSpan(28)));
        Assert.Equal(first[44..].Concat(second[44..]), combined[44..]);
    }

    [Fact]
    public void Combine_SingleSegmentReturnedAsIs()
    {
        var wav = Wav(3);
        Assert.Same(wav, RecorderWavSegments.Combine([wav]));
    }

    [Fact]
    public void Combine_ClampsAtMaximumSamples()
    {
        var wav = Wav(RecorderWavSegments.MaximumSamples);
        var combined = RecorderWavSegments.Combine([wav, Wav(2)]);
        Assert.Equal(RecorderWavSegments.MaximumSamples, RecorderWavSegments.SampleCount(combined));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(34)]
    [InlineData(40)]
    public void Combine_WrongFormatThrows(int offset)
    {
        var wav = Wav(1);
        wav[offset]++;
        Assert.Throws<InvalidDataException>(() => RecorderWavSegments.Combine([wav]));
    }

    [Fact]
    public void Combine_NoPayloadReturnsEmpty()
    {
        Assert.Empty(RecorderWavSegments.Combine([]));
        Assert.Empty(RecorderWavSegments.Combine([Wav(0)]));
    }

    private static byte[] Wav(int samples)
    {
        var wav = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(wav);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), samples * 2);
        return wav;
    }
}
