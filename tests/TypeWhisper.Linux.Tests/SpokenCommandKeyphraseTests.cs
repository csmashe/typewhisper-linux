using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.SpokenCommand;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpokenCommandKeyphraseTests
{
    private const string Keyphrase = AppSettings.DefaultCommandKeyphrase;

    [Theory]
    [InlineData("TypeWhisper, translate this", "translate this")]
    [InlineData("TypeWhisper translate this", "translate this")]
    [InlineData("type whisper make it shorter", "make it shorter")]
    [InlineData("typewhisperer summarize", "summarize")]
    [InlineData("TypeWhisper: write a note", "write a note")]
    [InlineData("TypeWhisper - fix the grammar", "fix the grammar")]
    [InlineData("  TypeWhisper   make this formal  ", "make this formal")]
    public void TryStrip_ExtractsCommandAfterKeyphrase(string rawText, string expectedCommand)
    {
        var stripped = SpokenCommandKeyphrase.TryStrip(rawText, Keyphrase, out var command);

        Assert.True(stripped);
        Assert.Equal(expectedCommand, command);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("TypeWhisper")]
    [InlineData("TypeWhisper.")]
    [InlineData("type whisper")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryStrip_ReturnsFalseWhenNoCommandFollows(string rawText)
    {
        var stripped = SpokenCommandKeyphrase.TryStrip(rawText, Keyphrase, out var command);

        Assert.False(stripped);
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void TryStrip_ReturnsFalseForBlankKeyphrase()
    {
        var stripped = SpokenCommandKeyphrase.TryStrip("anything at all", "   ", out var command);

        Assert.False(stripped);
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void TryStrip_HonorsCustomMultiWordKeyphrase()
    {
        var stripped = SpokenCommandKeyphrase.TryStrip(
            "hey type, draft an email",
            "hey type",
            out var command
        );

        Assert.True(stripped);
        Assert.Equal("draft an email", command);
    }

    [Fact]
    public void TryStrip_DoesNotFalseTriggerOnUnrelatedLeadingWords()
    {
        var stripped = SpokenCommandKeyphrase.TryStrip(
            "typing these words quickly",
            Keyphrase,
            out var command
        );

        Assert.False(stripped);
        Assert.Equal(string.Empty, command);
    }
}
