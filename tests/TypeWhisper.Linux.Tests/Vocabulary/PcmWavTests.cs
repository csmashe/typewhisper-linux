using System.Text;
using TypeWhisper.Linux.Services.Vocabulary;
using Xunit;

namespace TypeWhisper.Linux.Tests.Vocabulary;

public sealed class PcmWavTests
{
    private static readonly float[] s_half = [0.5f];

    [Fact]
    public void StandardHeader_RoundTripsSamples() =>
        Assert.Equal(
            new[] { -1, -0.5f, 0, 0.5f, 32767 / 32768f },
            PcmWav.ToMonoSamples16K(CreateWav([-32768, -16384, 0, 16384, 32767]))
        );

    [Theory]
    [InlineData(uint.MaxValue)]
    [InlineData(1000u)]
    public void PipeHeaderWithList_DecodesToEnd(uint dataSize) =>
        Assert.Equal(
            new[] { -1, 0, 0.5f },
            PcmWav.ToMonoSamples16K(CreateWav([-32768, 0, 16384], list: true, dataSize: dataSize))
        );

    [Theory]
    [InlineData(2, 16000, 16, 1)]
    [InlineData(1, 44100, 16, 1)]
    [InlineData(1, 16000, 8, 1)]
    [InlineData(1, 16000, 16, 3)]
    public void UnsupportedFormat_ReturnsEmpty(int channels, int rate, int bits, int format) =>
        Assert.Empty(PcmWav.ToMonoSamples16K(CreateWav([123], channels, rate, bits, format)));

    [Fact]
    public void TruncatedHeaders_ReturnEmpty()
    {
        var wav = CreateWav([123]);
        for (var length = 0; length < 44; length++)
            Assert.Empty(PcmWav.ToMonoSamples16K(wav[..length]));
    }

    [Fact]
    public void OddTrailingByte_IsDropped()
    {
        var wav = CreateWav([16384], dataSize: uint.MaxValue);
        Assert.Equal(s_half, PcmWav.ToMonoSamples16K([.. wav, 255]));
    }

    internal static byte[] CreateWav(
        short[] samples,
        int channels = 1,
        int rate = 16000,
        int bits = 16,
        int format = 1,
        bool list = false,
        uint? dataSize = null
    )
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(list ? uint.MaxValue : (uint)(36 + samples.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)format);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bits / 8);
        writer.Write((ushort)(channels * bits / 8));
        writer.Write((ushort)bits);
        if (list)
        {
            writer.Write("LIST"u8);
            writer.Write(3);
            writer.Write(new byte[] { 1, 2, 3, 0 });
        }
        writer.Write("data"u8);
        writer.Write(dataSize ?? (uint)(samples.Length * 2));
        foreach (var sample in samples)
            writer.Write(sample);
        return stream.ToArray();
    }
}
