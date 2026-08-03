using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Launcher discovery tests. The failure they guard against is silent: an
///     undiscovered browser is patched by nothing, yet
///     <see cref="BrowserAccessibilitySetupHelper.Status.IsFullyConfigured" /> reports
///     success vacuously, since "not installed" and "installed and patched" reach the
///     same verdict. Env vars are mutated in place, safe only because the assembly
///     disables test parallelization.
/// </summary>
public sealed class BrowserAccessibilityLauncherDiscoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Join(
        Path.GetTempPath(),
        "tw-launcher-discovery-" + Guid.NewGuid().ToString("N")
    );

    private readonly string? _originalHome = Environment.GetEnvironmentVariable("HOME");
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _originalDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");

    public BrowserAccessibilityLauncherDiscoveryTests()
    {
        Directory.CreateDirectory(_tempDir);
        // An isolated HOME keeps the developer's real browsers out of the assertions.
        Environment.SetEnvironmentVariable("HOME", _tempDir);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Join(_tempDir, "data-home"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", _originalDataDirs);
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public void PerUserFlatpakExportDirectory_LeadsPrecedence()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/usr/share");

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Equal(
            Path.Join(_tempDir, "data-home", "flatpak", "exports", "share", "applications"),
            dirs[0]
        );
    }

    [Fact]
    public void DataDirsAreHonouredInDeclaredOrder()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/opt/first/share:/opt/second/share");

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Equal("/opt/first/share/applications", dirs[1]);
        Assert.Equal("/opt/second/share/applications", dirs[2]);
    }

    [Fact]
    public void FlatpakExportRootsSurviveAnIncompleteDataDirs()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/opt/only/share");

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Contains("/var/lib/flatpak/exports/share/applications", dirs);
    }

    [Fact]
    public void SystemRootsOmittedByTheSessionAreNotReintroduced()
    {
        // A root the session left out is one whose launchers the desktop never reads,
        // so patching a copy from it would shadow the menu with an unreachable build.
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/opt/only/share");

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.DoesNotContain("/usr/share/applications", dirs);
        Assert.DoesNotContain("/usr/local/share/applications", dirs);
    }

    [Fact]
    public void DuplicateAndRelativeEntriesAreDropped()
    {
        // A duplicate must never outrank a higher-precedence source.
        Environment.SetEnvironmentVariable(
            "XDG_DATA_DIRS",
            "/usr/share/:/usr/share:relative/share:/usr/local/share"
        );

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Single(dirs, d => d.TrimEnd('/') == "/usr/share/applications");
        Assert.DoesNotContain(dirs, d => d.Contains("relative", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsetDataDirsFallsBackToSpecDefaults()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", null);

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Contains("/usr/local/share/applications", dirs);
        Assert.Contains("/usr/share/applications", dirs);
    }

    [Theory]
    [InlineData("org.chromium.Chromium.desktop")]
    [InlineData("com.google.Chrome.desktop")]
    [InlineData("com.brave.Browser.desktop")]
    [InlineData("com.microsoft.Edge.desktop")]
    [InlineData("org.mozilla.firefox.desktop")]
    [InlineData("app.zen_browser.zen.desktop")]
    public void UserFlatpakExport_IsFoundAndOutranksSystemCopy(string launcherName)
    {
        // Asserted on the resolved path, not a status flag: a machine with the same
        // launcher in /usr/share would satisfy the flag from the wrong copy.
        var exportDir = WriteLauncher(
            Path.Join(_tempDir, "data-home", "flatpak", "exports", "share", "applications"),
            launcherName
        );

        var found = BrowserAccessibilitySetupHelper.FindSystemLauncher(launcherName);

        Assert.Equal(Path.Join(exportDir, launcherName), found);
    }

    [Fact]
    public void UserFlatpakExport_DrivesInstalledStatus()
    {
        // Brave ships no launcher in the system dirs here or on a stock CI image, so an
        // installed verdict can only have come from the export directory.
        WriteLauncher(
            Path.Join(_tempDir, "data-home", "flatpak", "exports", "share", "applications"),
            "com.brave.Browser.desktop"
        );

        var status = BrowserAccessibilitySetupHelper.IsCurrentlyConfigured();

        Assert.True(status.ChromiumInstalled);
        // Discovered but unpatched: the status must not claim the work is done.
        Assert.False(status.ChromiumLauncherPresent);
        Assert.False(status.IsFullyConfigured);
    }

    [Fact]
    public void RelativeDataHome_FallsBackToTheDefaultRoot()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", "relative/data");

        var dirs = BrowserAccessibilitySetupHelper.LauncherSourceDirectories().ToList();

        Assert.Equal(
            Path.Join(_tempDir, ".local", "share", "flatpak", "exports", "share", "applications"),
            dirs[0]
        );
    }

    [Fact]
    public void UnknownLauncherName_IsNotFound()
    {
        Assert.Null(
            BrowserAccessibilitySetupHelper.FindSystemLauncher("com.example.NotAThing.desktop")
        );
    }

    private static string WriteLauncher(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Join(dir, name),
            "[Desktop Entry]\nType=Application\nExec=/usr/bin/flatpak run org.example.App %U\n"
        );
        return dir;
    }
}
