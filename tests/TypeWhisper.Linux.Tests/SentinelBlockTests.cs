using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Sentinel-block round-trip tests. The Hyprland and Sway writers both
///     rely on the same helper so a bug here would corrupt either config.
/// </summary>
public sealed class SentinelBlockTests
{
    [Fact]
    public void Scan_EmptyFile_ReportsNoBlock()
    {
        var scan = SentinelBlock.Scan(string.Empty);
        Assert.False(scan.Mismatched);
        Assert.Null(scan.OpenLine);
        Assert.Null(scan.CloseLine);
    }

    [Fact]
    public void Scan_FileWithoutSentinels_ReportsNoBlock()
    {
        var scan = SentinelBlock.Scan("bind = SUPER, q, killactive\n");
        Assert.False(scan.Mismatched);
        Assert.Null(scan.OpenLine);
    }

    [Fact]
    public void Scan_WellFormedBlock_ReportsMatchedPair()
    {
        var input =
            "bind = SUPER, q, killactive\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + "bind  = CTRL SHIFT, SPACE, exec, typewhisper record start\n"
            + SentinelBlock.CloseSentinel
            + "\n";
        var scan = SentinelBlock.Scan(input);
        Assert.False(scan.Mismatched);
        Assert.Equal(1, scan.OpenLine);
        Assert.Equal(3, scan.CloseLine);
    }

    [Fact]
    public void Scan_MissingCloseSentinel_ReportsMismatch()
    {
        var input = SentinelBlock.OpenSentinel + "\nbind = ...\n";
        var scan = SentinelBlock.Scan(input);
        Assert.True(scan.Mismatched);
    }

    [Fact]
    public void Scan_DuplicateOpenSentinel_ReportsMismatch()
    {
        var input =
            SentinelBlock.OpenSentinel
            + "\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + SentinelBlock.CloseSentinel
            + "\n";
        var scan = SentinelBlock.Scan(input);
        Assert.True(scan.Mismatched);
    }

    [Fact]
    public void ReplaceOrAppend_NoBlock_AppendsAtEnd()
    {
        var input = "bind = SUPER, q, killactive\n";
        var output = SentinelBlock.ReplaceOrAppend(
            input,
            new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" }
        );
        Assert.Contains(SentinelBlock.OpenSentinel, output);
        Assert.Contains(SentinelBlock.CloseSentinel, output);
        Assert.Contains("typewhisper", output);
        Assert.Contains("killactive", output);
    }

    [Fact]
    public void ReplaceOrAppend_ExistingBlock_ReplacesInPlace()
    {
        var input =
            "bind = SUPER, q, killactive\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + "stale = junk\n"
            + SentinelBlock.CloseSentinel
            + "\n"
            + "bind = SUPER, x, exit\n";
        var output = SentinelBlock.ReplaceOrAppend(
            input,
            new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" }
        );
        Assert.DoesNotContain("stale = junk", output);
        Assert.Contains("typewhisper", output);
        // Trailing user content must survive.
        Assert.Contains("exit", output);
        // Exactly one open + close after the replace.
        Assert.Equal(1, CountOccurrences(output, SentinelBlock.OpenSentinel));
        Assert.Equal(1, CountOccurrences(output, SentinelBlock.CloseSentinel));
    }

    [Fact]
    public void ReplaceOrAppend_MismatchedBlock_Throws()
    {
        var input = SentinelBlock.OpenSentinel + "\nbind = ...\n";
        Assert.Throws<InvalidOperationException>(() =>
            SentinelBlock.ReplaceOrAppend(input, new[] { "bind = x" })
        );
    }

    [Fact]
    public void Remove_NoBlock_LeavesContentsUnchanged()
    {
        var input = "bind = SUPER, q, killactive\n";
        Assert.Equal(input, SentinelBlock.Remove(input));
    }

    [Fact]
    public void Remove_ExistingBlock_RemovesIt()
    {
        var input =
            "bind = SUPER, q, killactive\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + "bind  = CTRL SHIFT, SPACE, exec, typewhisper\n"
            + SentinelBlock.CloseSentinel
            + "\n";
        var output = SentinelBlock.Remove(input);
        Assert.DoesNotContain("typewhisper", output);
        Assert.DoesNotContain(SentinelBlock.OpenSentinel, output);
        Assert.Contains("killactive", output);
    }

    [Fact]
    public void ReplaceOrAppend_AppendToFileEndingWithNewline_PreservesTrailingNewline()
    {
        var input = "bind = SUPER, q, killactive\n";
        var output = SentinelBlock.ReplaceOrAppend(
            input,
            new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" }
        );
        Assert.EndsWith("\n", output);
    }

    [Fact]
    public void ReplaceOrAppend_AppendToFileWithoutTrailingNewline_StaysWithoutTrailingNewline()
    {
        var input = "bind = SUPER, q, killactive";
        var output = SentinelBlock.ReplaceOrAppend(
            input,
            new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" }
        );
        Assert.False(output.EndsWith("\n"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Remove_RoundTrip_PreservesTrailingNewlineState(bool trailingNewline)
    {
        var input =
            "bind = SUPER, q, killactive\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + "bind  = CTRL SHIFT, SPACE, exec, typewhisper\n"
            + SentinelBlock.CloseSentinel
            + (trailingNewline ? "\n" : string.Empty);
        var output = SentinelBlock.Remove(input);
        Assert.Equal(trailingNewline, output.EndsWith("\n"));
    }

    [Fact]
    public void ExtractBlockLines_NoBlock_ReturnsNull()
    {
        Assert.Null(SentinelBlock.ExtractBlockLines("bind = SUPER, q, killactive\n"));
    }

    [Fact]
    public void ExtractBlockLines_MismatchedBlock_ReturnsNull()
    {
        var input = SentinelBlock.OpenSentinel + "\nbind = ...\n";
        Assert.Null(SentinelBlock.ExtractBlockLines(input));
    }

    [Fact]
    public void ExtractBlockLines_WellFormedBlock_ReturnsInnerLines()
    {
        var managed = new[]
        {
            "bind  = CTRL SHIFT, SPACE, exec, typewhisper record start",
            "bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop"
        };
        var input =
            "bind = SUPER, q, killactive\n"
            + SentinelBlock.OpenSentinel
            + "\n"
            + string.Join("\n", managed)
            + "\n"
            + SentinelBlock.CloseSentinel
            + "\n";

        var inner = SentinelBlock.ExtractBlockLines(input);

        Assert.NotNull(inner);
        Assert.Equal(managed, inner);
    }

    [Fact]
    public void ExtractBlockLines_RoundTripsWithReplaceOrAppend()
    {
        // The installed-check (Hyprland/Sway IsInstalledAsync) relies on this:
        // what ReplaceOrAppend writes must be exactly what ExtractBlockLines
        // reads back, or a freshly-written shortcut would read as not-installed.
        var managed = new[] { "bindsym --no-repeat Ctrl+Shift+space exec typewhisper" };
        var written = SentinelBlock.ReplaceOrAppend("bindsym $mod+q kill\n", managed);
        Assert.Equal(managed, SentinelBlock.ExtractBlockLines(written));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}