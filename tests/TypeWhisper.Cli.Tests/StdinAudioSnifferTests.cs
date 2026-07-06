using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class StdinAudioSnifferTests
{
    [Fact]
    public void DetectsWavMagic()
    {
        var bytes = "RIFF\0\0\0\0WAVE"u8.ToArray();
        Assert.Equal("wav", StdinAudioSniffer.Detect(bytes));
    }

    [Fact]
    public void DetectsFlacMagic()
    {
        Assert.Equal("flac", StdinAudioSniffer.Detect("fLaC"u8.ToArray()));
    }

    [Fact]
    public void DetectsOggMagic()
    {
        Assert.Equal("ogg", StdinAudioSniffer.Detect("OggS"u8.ToArray()));
    }

    [Fact]
    public void DetectsId3Mp3()
    {
        Assert.Equal("mp3", StdinAudioSniffer.Detect("ID3"u8.ToArray()));
    }

    [Fact]
    public void DetectsMp3FrameSync()
    {
        Assert.Equal("mp3", StdinAudioSniffer.Detect([0xFF, 0xFB]));
        Assert.Equal("mp3", StdinAudioSniffer.Detect([0xFF, 0xF3]));
    }

    [Fact]
    public void DefaultsToWavOnUnknownHeader()
    {
        Assert.Equal("wav", StdinAudioSniffer.Detect("random   "u8.ToArray()));
    }

    [Fact]
    public void DefaultsToWavOnShortBuffer()
    {
        Assert.Equal("wav", StdinAudioSniffer.Detect(ReadOnlySpan<byte>.Empty));
    }
}
