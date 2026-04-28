using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class VoiceCommandParserTests
{
    private readonly VoiceCommandParser _sut = new();

    [Theory]
    [InlineData("Hello world press enter", "Hello world")]
    [InlineData("Hello world, press enter.", "Hello world")]
    [InlineData("Hello world PRESS ENTER!", "Hello world")]
    public void Parse_PressEnterSuffix_RemovesCommandAndSetsAutoEnter(string input, string expectedText)
    {
        var result = _sut.Parse(input);

        Assert.Equal(expectedText, result.Text);
        Assert.True(result.AutoEnter);
        Assert.False(result.CancelInsertion);
    }

    [Fact]
    public void Parse_PressEnterAlone_SendsEnterWithoutText()
    {
        var result = _sut.Parse("press enter");

        Assert.Equal("", result.Text);
        Assert.True(result.AutoEnter);
        Assert.False(result.CancelInsertion);
    }

    [Theory]
    [InlineData("Hello new line", "Hello\n")]
    [InlineData("Hello new paragraph", "Hello\n\n")]
    public void Parse_LineBreakSuffixes_ConvertToNewlines(string input, string expectedText)
    {
        var result = _sut.Parse(input);

        Assert.Equal(expectedText, result.Text);
        Assert.False(result.AutoEnter);
    }

    [Fact]
    public void Parse_ChainedSuffixCommands_AppliesAllSuffixes()
    {
        var result = _sut.Parse("Hello new paragraph press enter");

        Assert.Equal("Hello\n\n", result.Text);
        Assert.True(result.AutoEnter);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("cancel.")]
    [InlineData("press enter cancel")]
    public void Parse_CancelWithoutMeaningfulContent_CancelsInsertion(string input)
    {
        var result = _sut.Parse(input);

        Assert.Equal("", result.Text);
        Assert.True(result.CancelInsertion);
    }

    [Fact]
    public void Parse_NonSuffixPhrasesRemainNormalText()
    {
        var result = _sut.Parse("Please write the words press enter in the note");

        Assert.Equal("Please write the words press enter in the note", result.Text);
        Assert.False(result.AutoEnter);
        Assert.False(result.CancelInsertion);
    }
}
