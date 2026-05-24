using TypeWhisper.Cli;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Port_OutOfRange_Errors()
    {
        var options = Program.CliOptions.Parse(["--port", "70000", "status"]);
        Assert.NotNull(options.Error);
        Assert.Contains("1 and 65535", options.Error);
    }

    [Fact]
    public void Token_FollowedByFlag_FailsCleanly()
    {
        var options = Program.CliOptions.Parse(["--token", "--json", "status"]);
        Assert.NotNull(options.Error);
        Assert.Contains("requires a value", options.Error);
    }

    [Fact]
    public void ApiTokenAlias_MapsToToken()
    {
        var options = Program.CliOptions.Parse(["--api-token", "abc123", "status"]);
        Assert.Null(options.Error);
        Assert.Equal("abc123", options.Token);
        Assert.True(options.TokenWasExplicit);
        Assert.Equal("status", options.Command);
    }

    [Fact]
    public void ExplicitPort_SetsExplicitFlag()
    {
        var options = Program.CliOptions.Parse(["--port", "8080", "status"]);
        Assert.Equal(8080, options.Port);
        Assert.True(options.PortWasExplicit);
    }

    [Fact]
    public void NoExplicitPort_DefaultsAndUnflagged()
    {
        var options = Program.CliOptions.Parse(["status"]);
        Assert.Equal(9876, options.Port);
        Assert.False(options.PortWasExplicit);
    }
}

public class StdinAudioSnifferTests
{
    [Fact]
    public void DetectsWavMagic()
    {
        var bytes = new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            0, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E'
        };
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
        Assert.Equal("mp3", StdinAudioSniffer.Detect(new byte[] { 0xFF, 0xFB }));
        Assert.Equal("mp3", StdinAudioSniffer.Detect(new byte[] { 0xFF, 0xF3 }));
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
