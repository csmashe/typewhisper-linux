using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     AtomicFileWriter must never replace a symlinked destination with a regular file.
///     Dotfile managers (stow/chezmoi/home-manager) commonly manage compositor configs
///     (hyprland.conf, sway config) as symlinks; losing that link on TypeWhisper's first
///     write silently breaks the user's dotfile setup (audit §4 M8).
/// </summary>
public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteAsync_PlainRegularFile_OverwritesContentAndLeavesNoTempFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");

            await AtomicFileWriter.WriteAsync(path, "new", CancellationToken.None);

            Assert.Equal("new", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_NothingExistsYet_CreatesRegularFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");

            await AtomicFileWriter.WriteAsync(path, "fresh install", CancellationToken.None);

            Assert.Equal("fresh install", await File.ReadAllTextAsync(path));
            Assert.Null(new FileInfo(path).LinkTarget);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path)
                );
            }
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_ExistingFile_PreservesExactMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            const UnixFileMode mode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(path, mode);

            await AtomicFileWriter.WriteAsync(path, "new", CancellationToken.None);

            Assert.Equal(mode, File.GetUnixFileMode(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_RegularFile_ReplacesMatchingSnapshot()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "new",
                CancellationToken.None
            );

            Assert.True(committed);
            Assert.Equal("new", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_EditAfterCapture_ReportsConflictAndPreservesEdit()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            await File.WriteAllTextAsync(path, "user edit");

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "typewhisper edit",
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.Equal("user edit", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_ModeChangedAfterCapture_ReportsConflict()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            File.SetUnixFileMode(path, UnixFileMode.UserRead);

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "new",
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.Equal("old", await File.ReadAllTextAsync(path));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsDirectory_RefusesNonRegularEntry()
    {
        var dir = CreateTempDir();
        try
        {
            var target = Path.Join(dir, "hyprland.conf");
            Directory.CreateDirectory(target);

            await Assert.ThrowsAsync<IOException>(
                () => AtomicFileWriter.WriteAsync(target, "new", CancellationToken.None)
            );

            Assert.True(Directory.Exists(target));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_FileCreatedAfterMissingCapture_ReportsConflict()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            await File.WriteAllTextAsync(path, "user-created config");

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "typewhisper config",
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.Equal("user-created config", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_DeletesOnlyMatchingDirectFile()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "user.js");
            await File.WriteAllTextAsync(path, "owned block");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);

            Assert.True(
                await AtomicFileWriter.DeleteIfUnchangedAsync(
                    snapshot,
                    CancellationToken.None
                )
            );
            Assert.False(File.Exists(path));

            await File.WriteAllTextAsync(path, "original");
            snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            await File.WriteAllTextAsync(path, "external edit");
            Assert.False(
                await AtomicFileWriter.DeleteIfUnchangedAsync(
                    snapshot,
                    CancellationToken.None
                )
            );
            Assert.Equal("external edit", await File.ReadAllTextAsync(path));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_RefusesToDeleteThroughASymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = CreateTempDir();
        try
        {
            var target = Path.Join(dir, "target.js");
            var link = Path.Join(dir, "user.js");
            await File.WriteAllTextAsync(target, "foreign content");
            File.CreateSymbolicLink(link, target);
            var snapshot = await AtomicFileWriter.CaptureAsync(link, CancellationToken.None);

            Assert.False(
                await AtomicFileWriter.DeleteIfUnchangedAsync(
                    snapshot,
                    CancellationToken.None
                )
            );
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal("foreign content", await File.ReadAllTextAsync(target));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsSymlinkToRegularFile_PreservesLinkAndUpdatesRealFile()
    {
        var dir = CreateTempDir();
        try
        {
            var real = Path.Join(dir, "real-hyprland.conf"); // the dotfile manager's repo copy
            await File.WriteAllTextAsync(real, "old");
            var link = Path.Join(dir, "hyprland.conf"); // e.g. ~/.config/hypr/hyprland.conf
            File.CreateSymbolicLink(link, real);

            await AtomicFileWriter.WriteAsync(link, "new", CancellationToken.None);

            Assert.NotNull(new FileInfo(link).LinkTarget); // still a symlink
            Assert.Equal(real, File.ResolveLinkTarget(link, true)!.FullName);
            Assert.Equal("new", await File.ReadAllTextAsync(real));
            Assert.Equal("new", await File.ReadAllTextAsync(link));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_SymlinkTarget_PreservesLinkAndUpdatesFinalFile()
    {
        var dir = CreateTempDir();
        try
        {
            var real = Path.Join(dir, "real-hyprland.conf");
            await File.WriteAllTextAsync(real, "old");
            var link = Path.Join(dir, "hyprland.conf");
            File.CreateSymbolicLink(link, real);
            var snapshot = await AtomicFileWriter.CaptureAsync(link, CancellationToken.None);

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "new",
                CancellationToken.None
            );

            Assert.True(committed);
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal(real, File.ResolveLinkTarget(link, true)!.FullName);
            Assert.Equal("new", await File.ReadAllTextAsync(real));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_FinalTargetEdited_ReportsConflictAndPreservesSymlink()
    {
        var dir = CreateTempDir();
        try
        {
            var real = Path.Join(dir, "real-hyprland.conf");
            await File.WriteAllTextAsync(real, "old");
            var link = Path.Join(dir, "hyprland.conf");
            File.CreateSymbolicLink(link, real);
            var snapshot = await AtomicFileWriter.CaptureAsync(link, CancellationToken.None);
            await File.WriteAllTextAsync(real, "user edit in final target");

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "typewhisper edit",
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal(real, File.ResolveLinkTarget(link, true)!.FullName);
            Assert.Equal("user edit in final target", await File.ReadAllTextAsync(real));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_SymlinkRetargeted_ReportsConflictAndPreservesBothFiles()
    {
        var dir = CreateTempDir();
        try
        {
            var original = Path.Join(dir, "original.conf");
            var retargeted = Path.Join(dir, "retargeted.conf");
            await File.WriteAllTextAsync(original, "original user content");
            await File.WriteAllTextAsync(retargeted, "retargeted user content");
            var link = Path.Join(dir, "hyprland.conf");
            File.CreateSymbolicLink(link, original);
            var snapshot = await AtomicFileWriter.CaptureAsync(link, CancellationToken.None);
            File.Delete(link);
            File.CreateSymbolicLink(link, retargeted);

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "typewhisper edit",
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.Equal(retargeted, File.ResolveLinkTarget(link, true)!.FullName);
            Assert.Equal("original user content", await File.ReadAllTextAsync(original));
            Assert.Equal("retargeted user content", await File.ReadAllTextAsync(retargeted));
            Assert.Equal("retargeted user content", await File.ReadAllTextAsync(link));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsRelativeSymlink_ResolvesAndPreservesLink()
    {
        var dir = CreateTempDir();
        try
        {
            var real = Path.Join(dir, "real.conf");
            await File.WriteAllTextAsync(real, "old");
            var subDir = Path.Join(dir, "hypr");
            Directory.CreateDirectory(subDir);
            var link = Path.Join(subDir, "hyprland.conf");
            File.CreateSymbolicLink(link, "../real.conf"); // relative target, like a stow symlink

            await AtomicFileWriter.WriteAsync(link, "new", CancellationToken.None);

            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Equal("new", await File.ReadAllTextAsync(real));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsChainedSymlink_ResolvesToFinalFileAndPreservesChain()
    {
        var dir = CreateTempDir();
        try
        {
            var real = Path.Join(dir, "real.conf");
            await File.WriteAllTextAsync(real, "old");
            var middle = Path.Join(dir, "middle.conf");
            File.CreateSymbolicLink(middle, real);
            var link = Path.Join(dir, "hyprland.conf");
            File.CreateSymbolicLink(link, middle);

            await AtomicFileWriter.WriteAsync(link, "new", CancellationToken.None);

            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.NotNull(new FileInfo(middle).LinkTarget);
            Assert.Equal("new", await File.ReadAllTextAsync(real));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsBrokenSymlink_ThrowsAndDoesNotCreateRegularFileAtLinkPath()
    {
        var dir = CreateTempDir();
        try
        {
            var link = Path.Join(dir, "hyprland.conf");
            File.CreateSymbolicLink(link, Path.Join(dir, "does-not-exist.conf"));

            await Assert.ThrowsAsync<IOException>(
                () => AtomicFileWriter.WriteAsync(link, "new", CancellationToken.None)
            );

            // The symlink must survive untouched -- no silent fallback to a regular file.
            Assert.NotNull(new FileInfo(link).LinkTarget);
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_TargetIsSymlinkCycle_ThrowsIOException()
    {
        var dir = CreateTempDir();
        try
        {
            var a = Path.Join(dir, "a.conf");
            var b = Path.Join(dir, "b.conf");
            File.CreateSymbolicLink(a, b);
            File.CreateSymbolicLink(b, a);

            await Assert.ThrowsAsync<IOException>(
                () => AtomicFileWriter.WriteAsync(a, "new", CancellationToken.None)
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_OrdersFileSyncThenRenameThenDirectorySync()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            var calls = new List<string>();

            // The hooks prove the publication order from observable filesystem state: the old
            // destination remains at file-sync time; the new one is present, with the temp
            // sibling consumed, at directory-sync time.
            await AtomicFileWriter.WriteAsync(
                path,
                "new",
                new AtomicFileWriter.SyncHooks(
                    (candidate, _) =>
                    {
                        calls.Add("file-sync");
                        Assert.EndsWith(".tmp", candidate, StringComparison.Ordinal);
                        Assert.True(File.Exists(candidate));
                        Assert.Equal("old", File.ReadAllText(path));
                    },
                    syncedDirectory =>
                    {
                        calls.Add("directory-sync");
                        Assert.Equal(dir, syncedDirectory);
                        Assert.Equal("new", File.ReadAllText(path));
                        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
                    }
                ),
                CancellationToken.None
            );

            Assert.Equal(["file-sync", "directory-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_CommitSyncsDirectoryAfterRename()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            var calls = new List<string>();

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "new",
                new AtomicFileWriter.SyncHooks(
                    (_, _) => calls.Add("file-sync"),
                    syncedDirectory =>
                    {
                        calls.Add("directory-sync");
                        Assert.Equal(dir, syncedDirectory);
                        Assert.Equal("new", File.ReadAllText(path));
                        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
                    }
                ),
                CancellationToken.None
            );

            Assert.True(committed);
            Assert.Equal(["file-sync", "directory-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteIfUnchangedAsync_Conflict_NeverReachesDirectorySync()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            await File.WriteAllTextAsync(path, "changed behind our back");
            var calls = new List<string>();

            var committed = await AtomicFileWriter.WriteIfUnchangedAsync(
                snapshot,
                "new",
                new AtomicFileWriter.SyncHooks(
                    (_, _) => calls.Add("file-sync"),
                    _ => calls.Add("directory-sync")
                ),
                CancellationToken.None
            );

            Assert.False(committed);
            Assert.Equal(["file-sync"], calls);
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task WriteAsync_WhenDirectorySyncFails_ThrowsIndeterminateCommitException()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "hyprland.conf");
            await File.WriteAllTextAsync(path, "old");

            var error = await Assert.ThrowsAsync<AtomicFileWriteIndeterminateCommitException>(
                () => AtomicFileWriter.WriteAsync(
                    path,
                    "new",
                    new AtomicFileWriter.SyncHooks(
                        (_, _) => { },
                        _ => throw new InjectedDirectorySyncException()
                    ),
                    CancellationToken.None
                )
            );

            // The publish landed before the directory sync failed: the destination must hold
            // the new content and the error must say the commit is indeterminate, not lost.
            Assert.Contains("Indeterminate commit", error.Message, StringComparison.Ordinal);
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
            Assert.IsType<InjectedDirectorySyncException>(error.InnerException);
            Assert.Equal("new", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_SyncsDirectoryAfterUnlink()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Join(dir, "user.js");
            await File.WriteAllTextAsync(path, "contents");
            var snapshot = await AtomicFileWriter.CaptureAsync(path, CancellationToken.None);
            var directorySyncObserved = false;

            var deleted = await AtomicFileWriter.DeleteIfUnchangedAsync(
                snapshot,
                new AtomicFileWriter.SyncHooks(
                    (_, _) => { },
                    syncedDirectory =>
                    {
                        directorySyncObserved = true;
                        Assert.Equal(dir, syncedDirectory);
                        Assert.False(File.Exists(path));
                    }
                ),
                CancellationToken.None
            );

            Assert.True(deleted);
            Assert.True(directorySyncObserved);
        }
        finally
        {
            TestPaths.DeleteDirectory(dir);
        }
    }

    private sealed class InjectedDirectorySyncException : Exception;

    private static string CreateTempDir()
    {
        return TestPaths.CreateTempDirectory("tw-atomic-symlink");
    }
}
