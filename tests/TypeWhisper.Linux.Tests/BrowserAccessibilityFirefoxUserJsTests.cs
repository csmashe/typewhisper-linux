using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BrowserAccessibilityFirefoxUserJsTests : IDisposable
{
    private const string PreservedPrefix = "// TypeWhisper preserved: ";
    private const string OwnedPreference =
        "user_pref(\"accessibility.force_disabled\", -1);";

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.BrowserAccessibilityFirefoxUserJsTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Theory]
    [InlineData("user_pref(\"accessibility.force_disabled\", 0);")]
    [InlineData("  user_pref(\"accessibility.force_disabled\", 1);  ")]
    [InlineData("\tuser_pref \t( 'accessibility.force_disabled' , +1 ); // administrator override")]
    public void ForeignPreference_IsPreservedAndRestoredExactly(string foreignLine)
    {
        var userJsPath = NewUserJsPath();
        var original =
            "// keep this header\r\n"
            + foreignLine
            + "\r\nuser_pref(\"browser.keep_this\", true);";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        var patched = File.ReadAllText(userJsPath);
        Assert.Contains(PreservedPrefix + foreignLine + "\r\n", patched);
        Assert.Contains(OwnedPreference, patched);
        Assert.Equal(1, CountLiveForceDisabledLines(patched));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void SetupTwice_DoesNotStackPreservedOrOwnedEntries_AndRepeatRevertIsClean()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"accessibility.force_disabled\", 0);\n"
            + "user_pref(\"browser.keep_this\", true);\n";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        var patchedOnce = File.ReadAllText(userJsPath);

        Assert.False(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        var patchedTwice = File.ReadAllText(userJsPath);
        Assert.Equal(patchedOnce, patchedTwice);
        Assert.Equal(1, CountOccurrences(patchedTwice, PreservedPrefix));
        Assert.Equal(1, CountOccurrences(patchedTwice, "// Set by TypeWhisper"));
        Assert.Equal(1, CountLiveForceDisabledLines(patchedTwice));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
        Assert.False(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void SetupRevertSetupRevert_RestoresForeignEntryEveryTime()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"browser.before\", true);\n"
            + "\tuser_pref ( \"accessibility.force_disabled\" , 1 );\n"
            + "user_pref(\"browser.after\", false);";
        File.WriteAllText(userJsPath, original);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
            Assert.Equal(1, CountLiveForceDisabledLines(File.ReadAllText(userJsPath)));

            Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
            Assert.Equal(original, File.ReadAllText(userJsPath));
        }
    }

    [Fact]
    public void NoForeignPreference_LeavesExistingContentUnchangedAfterRevert()
    {
        var userJsPath = NewUserJsPath();
        const string original = "user_pref(\"browser.keep_this\", true);";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        var patched = File.ReadAllText(userJsPath);
        Assert.DoesNotContain(PreservedPrefix, patched);
        Assert.Contains(OwnedPreference, patched);

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void NoExistingUserJs_IsDeletedOnRevertWhenOnlyOwnedEntryRemains()
    {
        var userJsPath = NewUserJsPath();

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        Assert.True(File.Exists(userJsPath));
        Assert.DoesNotContain(PreservedPrefix, File.ReadAllText(userJsPath));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.False(File.Exists(userJsPath));
    }

    [Fact]
    public void MultipleForeignPreferences_AreAllPreservedAndRestoredExactly()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"accessibility.force_disabled\", 0);\n"
            + "user_pref(\"browser.keep_this\", true);\n"
            + "  user_pref(\"accessibility.force_disabled\", 1);  \n"
            + "\tuser_pref ( 'accessibility.force_disabled' , +1 ); // admin\n";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        var patched = File.ReadAllText(userJsPath);
        Assert.Equal(3, CountOccurrences(patched, PreservedPrefix));
        Assert.Equal(1, CountOccurrences(patched, "// Set by TypeWhisper"));
        Assert.Equal(1, CountLiveForceDisabledLines(patched));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void ReSetupAfterOwnedPairHandDeleted_DoesNotDoublePreserve()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"accessibility.force_disabled\", 0);\n"
            + "user_pref(\"browser.keep_this\", true);\n";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        // User hand-deletes our owned pair but keeps the preserved comment.
        var patched = File.ReadAllText(userJsPath);
        var withoutOwnedPair = patched
            .Replace(
                "// Set by TypeWhisper — required for AT-SPI URL detection on Wayland.\n",
                "",
                StringComparison.Ordinal
            )
            .Replace(OwnedPreference + "\n", "", StringComparison.Ordinal);
        File.WriteAllText(userJsPath, withoutOwnedPair);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        var reSetup = File.ReadAllText(userJsPath);
        Assert.Equal(1, CountOccurrences(reSetup, PreservedPrefix));
        Assert.Equal(1, CountOccurrences(reSetup, "// Set by TypeWhisper"));
        Assert.Equal(1, CountLiveForceDisabledLines(reSetup));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ForeignPreferenceAsFinalLineWithoutTrailingNewline_RoundTripsExactly(
        string lineEnding
    )
    {
        var userJsPath = NewUserJsPath();
        var original =
            "user_pref(\"browser.before\", true);"
            + lineEnding
            + "user_pref(\"accessibility.force_disabled\", 0);";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        var patched = File.ReadAllText(userJsPath);
        Assert.Equal(1, CountOccurrences(patched, PreservedPrefix));
        Assert.Contains(OwnedPreference, patched);
        Assert.Equal(1, CountLiveForceDisabledLines(patched));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void ForeignPreferenceSharingLineWithAnotherStatement_LeavesNeighborActive()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"accessibility.force_disabled\", 0); user_pref(\"browser.foo\", true);\n"
            + "user_pref(\"browser.keep_this\", true);\n";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        // The shared line must not be commented out — doing so would also disable the
        // unrelated browser.foo statement. Our appended -1 pref wins regardless.
        var patched = File.ReadAllText(userJsPath);
        Assert.DoesNotContain(PreservedPrefix, patched);
        Assert.Contains(
            "user_pref(\"accessibility.force_disabled\", 0); user_pref(\"browser.foo\", true);",
            patched
        );
        Assert.Contains(OwnedPreference, patched);

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void UserAuthoredNegativeOne_LeavesFileByteUntouched()
    {
        var userJsPath = NewUserJsPath();
        const string original =
            "user_pref(\"browser.before\", true);\n"
            + "user_pref(\"accessibility.force_disabled\", -1);\n";
        File.WriteAllText(userJsPath, original);

        // No rewrite: a dotfile-managed symlink and file metadata survive untouched.
        Assert.False(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
        Assert.DoesNotContain(PreservedPrefix, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void AppendAfterOwnedBlockWhenSetupOwnedSeparator_RevertKeepsLinesSeparate()
    {
        var userJsPath = NewUserJsPath();
        // No trailing newline: setup owns the separator newline it inserts before the block.
        const string original = "// user header comment with no trailing newline";
        File.WriteAllText(userJsPath, original);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));

        const string appended = "user_pref(\"browser.added_later\", true);\n";
        File.AppendAllText(userJsPath, appended);

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));

        // The header must not merge with the appended line (which would swallow the
        // appended pref into the header's // comment).
        var reverted = File.ReadAllText(userJsPath);
        Assert.Equal(original + "\n" + appended, reverted);
    }

    [Fact]
    public void ExistingUserJs_PatchAndRevertPreserveExactMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var userJsPath = NewUserJsPath();
        const string original = "user_pref(\"browser.keep_this\", true);\n";
        const UnixFileMode mode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.WriteAllText(userJsPath, original);
        File.SetUnixFileMode(userJsPath, mode);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        Assert.Equal(mode, File.GetUnixFileMode(userJsPath));
        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.Equal(mode, File.GetUnixFileMode(userJsPath));
        Assert.Equal(original, File.ReadAllText(userJsPath));
    }

    [Fact]
    public void SymlinkedUserJs_PatchAndRevertPreserveLinkAndFinalFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var userJsPath = NewUserJsPath();
        var realPath = Path.Join(_tempDir, "dotfiles-user.js");
        const string original = "user_pref(\"accessibility.force_disabled\", 0);\n";
        File.WriteAllText(realPath, original);
        File.CreateSymbolicLink(userJsPath, realPath);

        Assert.True(BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath));
        Assert.NotNull(new FileInfo(userJsPath).LinkTarget);
        Assert.Contains(OwnedPreference, File.ReadAllText(realPath));

        Assert.True(BrowserAccessibilitySetupHelper.RevertFirefoxUserJs(userJsPath));
        Assert.NotNull(new FileInfo(userJsPath).LinkTarget);
        Assert.Equal(original, File.ReadAllText(realPath));
    }

    [Fact]
    public void BrokenUserJsSymlink_IsRefusedWithoutReplacingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var userJsPath = NewUserJsPath();
        File.CreateSymbolicLink(userJsPath, Path.Join(_tempDir, "missing-user.js"));

        Assert.Throws<IOException>(() =>
            BrowserAccessibilitySetupHelper.PatchFirefoxUserJs(userJsPath)
        );
        Assert.NotNull(new FileInfo(userJsPath).LinkTarget);
    }

    private string NewUserJsPath()
    {
        var profileDir = Path.Join(_tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);
        return Path.Join(profileDir, "user.js");
    }

    private static int CountLiveForceDisabledLines(string content)
    {
        return content
            .Split('\n')
            .Count(
                line =>
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && line.Contains("accessibility.force_disabled", StringComparison.Ordinal)
            );
    }

    private static int CountOccurrences(string content, string value)
    {
        return content.Split(value).Length - 1;
    }
}
