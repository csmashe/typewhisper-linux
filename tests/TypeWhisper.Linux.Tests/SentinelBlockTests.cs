using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Sentinel-block round-trip tests. The Hyprland and Sway writers both
/// rely on the same helper so a bug here would corrupt either config.
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
            "bind = SUPER, q, killactive\n" +
            SentinelBlock.OpenSentinel + "\n" +
            "bind  = CTRL SHIFT, SPACE, exec, typewhisper record start\n" +
            SentinelBlock.CloseSentinel + "\n";
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
            SentinelBlock.OpenSentinel + "\n" +
            SentinelBlock.OpenSentinel + "\n" +
            SentinelBlock.CloseSentinel + "\n";
        var scan = SentinelBlock.Scan(input);
        Assert.True(scan.Mismatched);
    }

    [Fact]
    public void ReplaceOrAppend_NoBlock_AppendsAtEnd()
    {
        var input = "bind = SUPER, q, killactive\n";
        var output = SentinelBlock.ReplaceOrAppend(input, new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" });
        Assert.Contains(SentinelBlock.OpenSentinel, output);
        Assert.Contains(SentinelBlock.CloseSentinel, output);
        Assert.Contains("typewhisper", output);
        Assert.Contains("killactive", output);
    }

    [Fact]
    public void ReplaceOrAppend_ExistingBlock_ReplacesInPlace()
    {
        var input =
            "bind = SUPER, q, killactive\n" +
            SentinelBlock.OpenSentinel + "\n" +
            "stale = junk\n" +
            SentinelBlock.CloseSentinel + "\n" +
            "bind = SUPER, x, exit\n";
        var output = SentinelBlock.ReplaceOrAppend(input, new[] { "bind  = CTRL SHIFT, SPACE, exec, typewhisper" });
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
            SentinelBlock.ReplaceOrAppend(input, new[] { "bind = x" }));
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
            "bind = SUPER, q, killactive\n" +
            SentinelBlock.OpenSentinel + "\n" +
            "bind  = CTRL SHIFT, SPACE, exec, typewhisper\n" +
            SentinelBlock.CloseSentinel + "\n";
        var output = SentinelBlock.Remove(input);
        Assert.DoesNotContain("typewhisper", output);
        Assert.DoesNotContain(SentinelBlock.OpenSentinel, output);
        Assert.Contains("killactive", output);
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
