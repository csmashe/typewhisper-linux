using TypeWhisper.Linux.Services.Hotkey.DeSetup;
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
            Directory.Delete(dir, true);
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
        }
        finally
        {
            Directory.Delete(dir, true);
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
            Directory.Delete(dir, true);
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
            Directory.Delete(dir, true);
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
            Directory.Delete(dir, true);
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
            Directory.Delete(dir, true);
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
            Directory.Delete(dir, true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Join(
            Path.GetTempPath(),
            "tw-atomic-symlink-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        return dir;
    }
}
