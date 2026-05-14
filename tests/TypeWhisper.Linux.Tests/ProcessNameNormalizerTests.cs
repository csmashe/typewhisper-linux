using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Pins the normalization rules that downstream terminal/browser
/// detection and profile-process matching depend on. The reverse-DNS
/// cases are the load-bearing ones: Wayland providers (Window Calls,
/// Sway, Hyprland, KWin) typically emit wm_class / app_id in the
/// <c>tld.vendor.app</c> form, and every consumer expects the short
/// canonical name.
/// </summary>
public sealed class ProcessNameNormalizerTests
{
    [Theory]
    [InlineData("com.mitchellh.ghostty", "ghostty")]
    [InlineData("org.mozilla.firefox", "firefox")]
    [InlineData("org.gnome.Nautilus", "Nautilus")]
    [InlineData("com.github.IsmaelMartinez.teams_for_linux", "teams_for_linux")]
    public void CollapsesReverseDnsToLastSegment(string input, string expected)
    {
        Assert.Equal(expected, ProcessNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("ghostty", "ghostty")]
    [InlineData("firefox", "firefox")]
    [InlineData("libreoffice", "libreoffice")]
    public void LeavesShortNamesUntouched(string input, string expected)
    {
        Assert.Equal(expected, ProcessNameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("/usr/bin/firefox", "firefox")]
    public void StripsPathsAndExeExtension(string input, string expected)
    {
        Assert.Equal(expected, ProcessNameNormalizer.Normalize(input));
    }

    [Fact]
    public void LeavesSingleDotNamesUntouched()
    {
        // "chrome.app" has only one dot — not reverse-DNS, must not be
        // mangled into "app". Two-segment names are rare but real on disk.
        Assert.Equal("chrome.app", ProcessNameNormalizer.Normalize("chrome.app"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsEmptyForBlankInput(string? input)
    {
        Assert.Equal("", ProcessNameNormalizer.Normalize(input));
    }
}
