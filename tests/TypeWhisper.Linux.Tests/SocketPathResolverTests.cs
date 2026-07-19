using TypeWhisper.Core;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SocketPathResolverTests
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Fact]
    public void DefaultFallbackDirectory_IsRuntimeUnderBasePath()
    {
        Assert.Equal(
            Path.Join(TypeWhisperEnvironment.BasePath, "Runtime"),
            SocketPathResolver.DefaultFallbackDirectory
        );
    }

    [Fact]
    public void ResolveControlSocketPath_AbsentXdg_ReturnsDeterministicSecureFallback()
    {
        var tempRoot = TestPaths.CreateTempDirectory(
            nameof(ResolveControlSocketPath_AbsentXdg_ReturnsDeterministicSecureFallback)
        );
        var fallbackDirectory = Path.Join(tempRoot, "fallback-runtime");
        var originalXdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Directory.CreateDirectory(fallbackDirectory);
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(
                fallbackDirectory,
                PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            );
#pragma warning restore CA1416

            var firstPath = SocketPathResolver.ResolveControlSocketPath(fallbackDirectory);
            var secondPath = SocketPathResolver.ResolveControlSocketPath(fallbackDirectory);
            var expectedPath = Path.Join(fallbackDirectory, "control.sock");

            Assert.Equal(expectedPath, firstPath);
            Assert.Equal(expectedPath, secondPath);
            Assert.Equal(firstPath, secondPath);
            Assert.True(Directory.Exists(fallbackDirectory));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(fallbackDirectory));
#pragma warning restore CA1416
            Assert.False(File.Exists(expectedPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "XDG_RUNTIME_DIR",
                originalXdgRuntimeDirectory
            );
            TestPaths.DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveControlSocketPath_ValidXdg_ReturnsPrimaryWithoutTouchingFallback()
    {
        var tempRoot = TestPaths.CreateTempDirectory(
            nameof(ResolveControlSocketPath_ValidXdg_ReturnsPrimaryWithoutTouchingFallback)
        );
        var xdgRuntimeDirectory = Path.Join(tempRoot, "xdg-runtime");
        var fallbackDirectory = Path.Join(tempRoot, "fallback-runtime");
        var originalXdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdgRuntimeDirectory);
            Directory.CreateDirectory(xdgRuntimeDirectory);

            var path = SocketPathResolver.ResolveControlSocketPath(fallbackDirectory);
            var containingDirectory = Path.Join(xdgRuntimeDirectory, "typewhisper");
            var expectedPath = Path.Join(containingDirectory, "control.sock");

            Assert.Equal(expectedPath, path);
            Assert.True(Directory.Exists(containingDirectory));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(containingDirectory));
#pragma warning restore CA1416
            Assert.False(File.Exists(expectedPath));
            Assert.False(Directory.Exists(fallbackDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "XDG_RUNTIME_DIR",
                originalXdgRuntimeDirectory
            );
            TestPaths.DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveControlSocketPath_UnusableXdgChild_ReturnsSecureFallback()
    {
        var tempRoot = TestPaths.CreateTempDirectory(
            nameof(ResolveControlSocketPath_UnusableXdgChild_ReturnsSecureFallback)
        );
        var xdgRuntimeDirectory = Path.Join(tempRoot, "xdg-runtime");
        var fallbackDirectory = Path.Join(tempRoot, "fallback-runtime");
        var originalXdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdgRuntimeDirectory);
            Directory.CreateDirectory(xdgRuntimeDirectory);
            File.WriteAllText(Path.Join(xdgRuntimeDirectory, "typewhisper"), "blocking file");

            var path = SocketPathResolver.ResolveControlSocketPath(fallbackDirectory);
            var expectedPath = Path.Join(fallbackDirectory, "control.sock");

            Assert.Equal(expectedPath, path);
            Assert.True(Directory.Exists(fallbackDirectory));
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(fallbackDirectory));
#pragma warning restore CA1416
            Assert.False(File.Exists(expectedPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "XDG_RUNTIME_DIR",
                originalXdgRuntimeDirectory
            );
            TestPaths.DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ResolveControlSocketPath_BlockedFallback_ThrowsWithoutReturningSocketPath()
    {
        var tempRoot = TestPaths.CreateTempDirectory(
            nameof(ResolveControlSocketPath_BlockedFallback_ThrowsWithoutReturningSocketPath)
        );
        var fallbackDirectory = Path.Join(tempRoot, "blocked-fallback");
        var expectedSocketPath = Path.Join(fallbackDirectory, "control.sock");
        var originalXdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? socketPath = null;

        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            File.WriteAllText(fallbackDirectory, "blocking file");

            Assert.Throws<IOException>(() =>
                socketPath = SocketPathResolver.ResolveControlSocketPath(fallbackDirectory)
            );

            Assert.Null(socketPath);
            Assert.True(File.Exists(fallbackDirectory));
            Assert.False(Directory.Exists(fallbackDirectory));
            Assert.False(File.Exists(expectedSocketPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "XDG_RUNTIME_DIR",
                originalXdgRuntimeDirectory
            );
            TestPaths.DeleteDirectory(tempRoot);
        }
    }
}
