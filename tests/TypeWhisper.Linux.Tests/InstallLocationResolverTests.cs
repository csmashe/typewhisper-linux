using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class InstallLocationResolverTests
{
    [Fact]
    public void Resolve_PrefersRootedAppImagePath()
    {
        Assert.Equal(
            "/home/u/Apps/TypeWhisper.AppImage",
            InstallLocationResolver.Resolve(
                "/home/u/Apps/TypeWhisper.AppImage",
                "/tmp/.mount_x/usr/bin/typewhisper",
                "/tmp/.mount_x/usr/bin/"
            )
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative.AppImage")]
    public void Resolve_IgnoresBlankOrRelativeAppImagePath(string? appImagePath)
    {
        Assert.Equal(
            "/opt/typewhisper",
            InstallLocationResolver.Resolve(
                appImagePath, "/opt/typewhisper/typewhisper", "/tmp/.mount_x/usr/bin/"
            )
        );
    }

    [Fact]
    public void Resolve_FallsBackToBaseDirectoryWithoutProcessPath()
    {
        Assert.Equal(
            "/opt/typewhisper",
            InstallLocationResolver.Resolve(null, null, "/opt/typewhisper/")
        );
    }

    [Fact]
    public void Resolve_TrimsTrailingSeparator()
    {
        Assert.Equal(
            "/opt/typewhisper",
            InstallLocationResolver.Resolve(null, null, "/opt/typewhisper///")
        );
    }
}
