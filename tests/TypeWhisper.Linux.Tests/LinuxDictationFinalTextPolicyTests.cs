using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxDictationFinalTextPolicyTests
{
    [Fact]
    public void SelectRawText_CollapsesIssue90AdjacentRepeatedPhrase()
    {
        const string rawText =
            "It would be really cool if the amount of time that the preview bubble remains after you paste could be setable. " +
            "I am mindful of settings proliferation. And the current preview time. " +
            "is probably close to being right, if not a little bit on the wrong. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "But given that this is such a core part of the user interaction.";
        const string expected =
            "It would be really cool if the amount of time that the preview bubble remains after you paste could be setable. " +
            "I am mindful of settings proliferation. And the current preview time. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "But given that this is such a core part of the user interaction.";

        var result = LinuxDictationFinalTextPolicy.SelectRawText(rawText);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SelectRawText_CollapsesExactAdjacentRepeatedPhrase()
    {
        var result = LinuxDictationFinalTextPolicy.SelectRawText(
            "Please send the updated draft tomorrow morning. Please send the updated draft tomorrow morning. Thanks.");

        Assert.Equal("Please send the updated draft tomorrow morning. Thanks.", result);
    }

    [Fact]
    public void SelectRawText_PreservesShortIntentionalRepeats()
    {
        var result = LinuxDictationFinalTextPolicy.SelectRawText("Yes yes, that's right.");

        Assert.Equal("Yes yes, that's right.", result);
    }

    [Fact]
    public void SelectRawText_WhitespaceOnlyReturnsEmptyText()
    {
        var result = LinuxDictationFinalTextPolicy.SelectRawText("   ");

        Assert.Equal("", result);
    }

    [Fact]
    public void SelectRawText_CollapsesIssue108ShortAdjacentRepeatedPhrase()
    {
        const string rawText =
            "Now go back to the appearance screen. and set the and set the preview text size to the maximum. " +
            "Dictate more text and note that the size of the preview bubble text is unchanged.";
        const string expected =
            "Now go back to the appearance screen. and set the preview text size to the maximum. " +
            "Dictate more text and note that the size of the preview bubble text is unchanged.";

        var result = LinuxDictationFinalTextPolicy.SelectRawText(rawText);

        Assert.Equal(expected, result);
    }
}
