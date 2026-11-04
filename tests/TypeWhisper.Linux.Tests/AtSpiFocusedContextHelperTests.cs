using System.Reflection;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the pure, subprocess-free helpers of the focused-context harvest (Feature 03):
///     whitespace collapse, snippet combination + char cap, and the password-role skip.
///     Identity guards use an injected runner; no live accessibility or clipboard data is read.
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
    [InlineData("  Report  ", "Report", true)] // surrounding whitespace
    [InlineData("Report", "Report <2>", true)] // KWin numbers duplicate captions
    [InlineData("Report", "Report private", false)] // other window whose title extends the recorded one
    [InlineData("Report private", "Report", false)] // other window whose title is a prefix of the recorded one
    [InlineData("file.txt — Visual Studio Code", "file.txt", false)] // substring is not the same window
    [InlineData("REPORT", "Report", false)] // titles differ in case
    [InlineData("Report <2>", "Report", false)] // the suffix belongs to the compositor title only
    [InlineData("Report", "Report <x>", false)] // not a KWin duplicate number
    [InlineData("Compose - Gmail", "Inbox (5) - Gmail", false)] // different windows of the same app
    [InlineData("Doc A — Writer", "Sheet B — Calc", false)] // unrelated
    [InlineData(null, "anything", false)] // missing → cannot confirm
    [InlineData("anything", "   ", false)] // blank → cannot confirm
    public void TitlesRelate_MatchesOnlyTheRecordedWindow(string? frameTitle, string? recordedTitle, bool expected)
    {
        Assert.Equal(expected, AtSpiUrlExtractor.TitlesRelate(frameTitle, recordedTitle));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("qa-editor", null)]
    [InlineData(null, "QA Window")]
    public void Harvest_UnknownWindowIdentity_DoesNotReadAccessibilityData(string? process, string? title)
    {
        var runner = new RecordingRunner();
        var extractor = new AtSpiUrlExtractor(runner);
        runner.Calls.Clear();

        Assert.Null(extractor.TryHarvestFocusedContext(process, title, "TypeWhisper"));
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(23, "s \"\"", false)]
    [InlineData(23, "s \"Different Window\"", false)]
    [InlineData(23, "s \"QA Window\"", true)]
    [InlineData(79, "", false)]
    [InlineData(40, "s \"Password\"", false)]
    public void Harvest_WindowConfirmation_FailsClosedWithoutReadingElementText(
        int role,
        string frameName,
        bool expected
    )
    {
        var runner = new RecordingRunner((_, args) => new ProcessRunResult(
            true,
            false,
            0,
            args.Contains("GetRole") ? $"u {role}" : args.Contains("Name") ? frameName : "",
            ""
        ));
        var extractor = new AtSpiUrlExtractor(runner);
        runner.Calls.Clear();

        Assert.Equal(expected, InvokeFocusedNodeBelongsToWindow(extractor, "/org/example/qa/field", 40));
        Assert.DoesNotContain(runner.Calls, call => call.Args.Contains("GetText"));
    }

    [Theory]
    [InlineData("QA Window", "QA Window", false)] // identical titles: either window could be focused
    [InlineData("QA Window <2>", "QA Window", false)] // KWin numbered the duplicate caption
    [InlineData("QA Window", "QA Window private", true)] // distinct title is a different window
    [InlineData("QA Window", "", true)] // untitled sibling cannot be the recorded window
    public void Harvest_WindowConfirmation_RejectsWhenAnotherAppWindowHasTheSameTitle(
        string recordedTitle,
        string siblingName,
        bool expected
    )
    {
        var runner = SameAppWindowsRunner("QA Window", siblingName);
        var extractor = new AtSpiUrlExtractor(runner);
        runner.Calls.Clear();

        Assert.Equal(expected, InvokeFocusedNodeBelongsToWindow(extractor, "/qa/frame", 40, recordedTitle));
        Assert.DoesNotContain(runner.Calls, call => call.Args.Contains("GetText"));
    }

    [Fact]
    public void Harvest_WindowConfirmation_RejectsWhenVisitBudgetEndsBeforeOtherWindowsAreChecked()
    {
        var runner = SameAppWindowsRunner("QA Window", "QA Window private");
        var extractor = new AtSpiUrlExtractor(runner);

        Assert.False(InvokeFocusedNodeBelongsToWindow(extractor, "/qa/frame", 1));
    }

    // App "/qa/app" has two top-level children: the focused frame "/qa/frame" and "/qa/other".
    private static RecordingRunner SameAppWindowsRunner(string frameName, string siblingName)
    {
        return new RecordingRunner((_, args) =>
        {
            var stdout = args.Contains("GetRole") ? "u 23"
                : args.Contains("GetChildren") && args.Contains("/qa/app")
                    ? "a(so) 2 \":1.9\" \"/qa/frame\" \":1.9\" \"/qa/other\""
                : args.Contains("Name") && args.Contains("/qa/frame") ? $"s \"{frameName}\""
                : args.Contains("Name") && args.Contains("/qa/other") ? $"s \"{siblingName}\""
                : "";
            return new ProcessRunResult(true, false, 0, stdout, "");
        });
    }

    private static bool InvokeFocusedNodeBelongsToWindow(
        AtSpiUrlExtractor extractor,
        string focusedPath,
        int remainingVisits,
        string recordedTitle = "QA Window"
    )
    {
        var nodeType = typeof(AtSpiUrlExtractor).GetNestedType("AccessibleRef", BindingFlags.NonPublic)!;
        var app = Activator.CreateInstance(nodeType, [":1.9", "/qa/app"]);
        var node = Activator.CreateInstance(nodeType, [":1.9", focusedPath]);
        var check = typeof(AtSpiUrlExtractor).GetMethod(
            "FocusedNodeBelongsToWindow",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        object?[] checkArgs =
            ["unix:path=/owned-qa-bus", app, node, recordedTitle, remainingVisits, CancellationToken.None];
        return (bool)check.Invoke(extractor, checkArgs)!;
    }

    private sealed class RecordingRunner(
        Func<string, IReadOnlyList<string>, ProcessRunResult>? respond = null
    ) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((fileName, args.ToArray()));
            return Task.FromResult(respond?.Invoke(fileName, args)
                ?? new ProcessRunResult(true, false, 0, "", ""));
        }
    }
}
