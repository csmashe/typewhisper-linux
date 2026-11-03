using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the pure, subprocess-free helpers of the focused-context harvest (Feature 03):
///     whitespace collapse, snippet combination + char cap, and the password-role skip.
///     The AT-SPI walk itself is subprocess-bound and exercised via live logs, not here.
/// </summary>
public sealed class AtSpiFocusedContextHelperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("foo", "foo")]
    [InlineData("  foo   bar  ", "foo bar")]
    [InlineData("foo\n\nbar\t\tbaz", "foo bar baz")]
    [InlineData("line one\r\nline two", "line one line two")]
    public void CollapseWhitespace_NormalizesRunsToSingleSpaces(string? input, string expected)
    {
        Assert.Equal(expected, AtSpiUrlExtractor.CollapseWhitespace(input));
    }

    [Fact]
    public void CombineFocusedSnippets_AllEmpty_ReturnsNull()
    {
        Assert.Null(AtSpiUrlExtractor.CombineFocusedSnippets([], 2500));
        Assert.Null(AtSpiUrlExtractor.CombineFocusedSnippets([null, "", "   "], 2500));
    }

    [Fact]
    public void CombineFocusedSnippets_JoinsCollapsedSnippetsWithNewlines()
    {
        var result = AtSpiUrlExtractor.CombineFocusedSnippets(
            ["  focused   value ", "Nearby Label"],
            2500
        );

        Assert.Equal("focused value\nNearby Label", result);
    }

    [Fact]
    public void CombineFocusedSnippets_DropsAdjacentDuplicates()
    {
        // A labelled field commonly exposes the same string as both text and Name.
        var result = AtSpiUrlExtractor.CombineFocusedSnippets(
            ["Email", "Email", "user@example.com"],
            2500
        );

        Assert.Equal("Email\nuser@example.com", result);
    }

    [Fact]
    public void CombineFocusedSnippets_HardCapsTotalLength()
    {
        var first = new string('a', 2000);
        var second = new string('b', 2000);

        var result = AtSpiUrlExtractor.CombineFocusedSnippets([first, second], 2500);

        Assert.NotNull(result);
        Assert.True(result.Length <= 2500, $"Expected ≤2500 chars, saw {result.Length}.");
        // The first snippet fits in full; the second is truncated to what remains.
        Assert.StartsWith(first, result);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(0, false)]
    [InlineData(79, false)]
    [InlineData(23, false)]
    public void IsPasswordTextRole_MatchesOnlyRole40(int role, bool expected)
    {
        Assert.Equal(expected, AtSpiUrlExtractor.IsPasswordTextRole(role));
    }

    [Theory]
    [InlineData("file.txt — Visual Studio Code", "file.txt — Visual Studio Code", true)] // exact
    [InlineData("file.txt — Visual Studio Code", "file.txt", true)] // recorded title is a substring
    [InlineData("file.txt", "file.txt — Visual Studio Code", true)] // frame title is a substring
    [InlineData("FILE.TXT — VS CODE", "file.txt — vs code", true)] // case-insensitive
    [InlineData("Compose - Gmail", "Inbox (5) - Gmail", false)] // different windows of the same app
    [InlineData("Doc A — Writer", "Sheet B — Calc", false)] // unrelated
    [InlineData(null, "anything", false)] // missing → cannot confirm
    [InlineData("anything", "   ", false)] // blank → cannot confirm
    public void TitlesRelate_MatchesOnlyPlausiblySameWindow(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, AtSpiUrlExtractor.TitlesRelate(a, b));
    }
}
