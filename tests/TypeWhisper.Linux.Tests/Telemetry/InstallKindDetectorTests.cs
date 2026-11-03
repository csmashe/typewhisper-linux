using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class InstallKindDetectorTests
{
    [Theory]
    [InlineData("/tmp/TypeWhisper.AppImage", "/opt/typewhisper/typewhisper", "appimage")]
    [InlineData(null, "/opt/typewhisper/typewhisper", "system")]
    [InlineData(null, "/usr/lib/typewhisper/x", "system")]
    [InlineData(null, "/home/u/dev/bin/typewhisper", "local")]
    [InlineData(null, null, "local")]
    [InlineData("relative.AppImage", null, "local")]
    public void Detects_install_kind(string? appImage, string? processPath, string expected)
    {
        Assert.Equal(expected, InstallKindDetector.Detect(appImage, processPath));
    }
}
