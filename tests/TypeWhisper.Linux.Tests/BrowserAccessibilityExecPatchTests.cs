using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Pure-function tests for the Chromium <c>--force-renderer-accessibility</c>
/// flag insertion. The interesting cases are wrapped launchers — Flatpak and
/// env(1) — where naively inserting after the first space would route the
/// flag to the wrapper instead of the browser.
/// </summary>
public sealed class BrowserAccessibilityExecPatchTests
{
    private const string Flag = "--force-renderer-accessibility";

    [Fact]
    public void InsertsBeforeFieldCode_OnPlainLauncher()
    {
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=/usr/bin/chromium %U", Flag);
        Assert.Equal($"Exec=/usr/bin/chromium {Flag} %U", result);
    }

    [Fact]
    public void InsertsBeforeFieldCode_OnFlatpakLauncher()
    {
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=chromium org.chromium.Chromium %U",
            Flag);
        Assert.Equal(
            $"Exec=/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=chromium org.chromium.Chromium {Flag} %U",
            result);
    }

    [Fact]
    public void InsertsBeforeFlatpakEscapeMarker()
    {
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=/usr/bin/flatpak run com.google.Chrome @@u %U @@", Flag);
        Assert.Equal(
            $"Exec=/usr/bin/flatpak run com.google.Chrome {Flag} @@u %U @@",
            result);
    }

    [Fact]
    public void InsertsBeforeFieldCode_OnEnvWrappedLauncher()
    {
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=env GTK_USE_PORTAL=1 /usr/bin/chromium %U", Flag);
        Assert.Equal(
            $"Exec=env GTK_USE_PORTAL=1 /usr/bin/chromium {Flag} %U",
            result);
    }

    [Fact]
    public void AppendsFlag_WhenExecHasNoFieldCode()
    {
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=/usr/bin/chromium", Flag);
        Assert.Equal($"Exec=/usr/bin/chromium {Flag}", result);
    }

    [Fact]
    public void HandlesPercentEscape_AsNonFieldCode()
    {
        // A literal "%%" in an Exec line is an escaped percent, not a field
        // code — the second char isn't alphanumeric, so the flag still
        // appends at end.
        var result = BrowserAccessibilitySetupHelper.InsertChromiumFlag(
            "Exec=/usr/bin/chromium --user-data-dir=/tmp/%%foo", Flag);
        Assert.Equal(
            $"Exec=/usr/bin/chromium --user-data-dir=/tmp/%%foo {Flag}",
            result);
    }

    [Fact]
    public void FirefoxWrapper_PrependsEnvVarsToEveryExecLine()
    {
        // .desktop files frequently carry multiple Exec= lines (main entry
        // plus per-action shortcuts like "New Window", "New Private
        // Window"). Every one of them needs the env wrapper so menu
        // shortcuts also get accessibility.
        var input = string.Join('\n',
            "[Desktop Entry]",
            "Name=Firefox",
            "Exec=firefox %u",
            "[Desktop Action new-window]",
            "Exec=firefox --new-window %u");

        var patched = BrowserAccessibilitySetupHelper.PrependEnvWrapperToExecLines(input);

        Assert.Contains("Exec=env MOZ_ENABLE_ACCESSIBILITY=1 GTK_MODULES=gail:atk-bridge firefox %u", patched);
        Assert.Contains("Exec=env MOZ_ENABLE_ACCESSIBILITY=1 GTK_MODULES=gail:atk-bridge firefox --new-window %u", patched);
    }

    [Fact]
    public void FirefoxWrapper_LeavesAlreadyPatchedLinesUntouched()
    {
        // Running setup twice in a row shouldn't double-wrap. The skip
        // condition is the literal MOZ_ENABLE_ACCESSIBILITY= marker.
        var alreadyPatched = "Exec=env MOZ_ENABLE_ACCESSIBILITY=1 GTK_MODULES=gail:atk-bridge firefox %u";

        var result = BrowserAccessibilitySetupHelper.PrependEnvWrapperToExecLines(alreadyPatched);

        Assert.Equal(alreadyPatched, result);
    }
}
