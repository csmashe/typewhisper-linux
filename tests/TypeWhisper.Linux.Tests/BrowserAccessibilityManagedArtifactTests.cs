// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAll* is deliberate in these test assertions.
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BrowserAccessibilityManagedArtifactTests : IDisposable
{
    private const string ChromeDesktopId = "google-chrome.desktop";
    private const string OwnershipComment =
        "# Installed by TypeWhisper - patches Exec= for URL detection";
    private const UnixFileMode Mode0600 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = TestPaths.CreateTempDirectory("browser-managed-artifacts");
    private readonly string? _originalConfigHome = Environment.GetEnvironmentVariable(
        "XDG_CONFIG_HOME"
    );
    private readonly string? _originalDataHome = Environment.GetEnvironmentVariable(
        "XDG_DATA_HOME"
    );

    public BrowserAccessibilityManagedArtifactTests()
    {
        Assert.Contains(
            ChromeDesktopId,
            BrowserAccessibilitySetupHelper.GetLauncherNames(
                BrowserLauncherPatchMode.ChromiumRendererAccessibility
            )
        );
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", DataHome);
        BrowserAccessibilitySetupHelper.ManagedArtifactStateRootOverride = StateRoot;
        BrowserAccessibilitySetupHelper.SystemLauncherDirectoriesOverride = [SystemApps];
        BrowserAccessibilitySetupHelper.FirefoxProfileRootsOverride =
            [Path.Join(_root, "empty-firefox-roots")];
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalConfigHome);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        BrowserAccessibilitySetupHelper.ManagedArtifactStateRootOverride = null;
        BrowserAccessibilitySetupHelper.SystemLauncherDirectoriesOverride = null;
        BrowserAccessibilitySetupHelper.FirefoxProfileRootsOverride = null;
        try
        {
            TestPaths.DeleteDirectory(_root);
        }
        catch
        {
            // Best-effort cleanup for symlink fixtures.
        }
    }

    [Fact]
    public async Task Setup_refuses_and_preserves_foreign_environment_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(EnvPath)!);
        await File.WriteAllTextAsync(EnvPath, "USER_SETTING=keep\n");

        var result = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("USER_SETTING=keep\n", File.ReadAllText(EnvPath));
        Assert.Contains("untouched", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_refuses_environment_symlink_and_preserves_its_target()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EnvPath)!);
        var target = Path.Join(_root, "environment-target");
        await File.WriteAllTextAsync(target, "target bytes\n");
        File.CreateSymbolicLink(EnvPath, target);

        var result = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("target bytes\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(EnvPath).LinkTarget);
    }

    [Fact]
    public async Task Customized_published_environment_file_is_retained_on_remove()
    {
        var setup = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);
        await File.AppendAllTextAsync(EnvPath, "USER_CUSTOMIZATION=1\n");

        var removal = await BrowserAccessibilitySetupHelper.RemoveAsync(
            CancellationToken.None
        );

        Assert.True(setup.Success);
        Assert.True(removal.Success);
        Assert.Contains("USER_CUSTOMIZATION=1", File.ReadAllText(EnvPath));
        Assert.Contains("Left env file", removal.Message, StringComparison.Ordinal);
        Assert.True(
            File.Exists(
                Path.Join(StateRoot, "browser-accessibility-environment", "state.json")
            )
        );
    }

    [Fact]
    public async Task Environment_file_honors_XDG_CONFIG_HOME_and_uses_private_mode()
    {
        var setup = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);

        Assert.True(setup.Success);
        Assert.True(File.Exists(EnvPath));
        Assert.StartsWith(
            ConfigHome + Path.DirectorySeparatorChar,
            EnvPath,
            StringComparison.Ordinal
        );
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode0600, File.GetUnixFileMode(EnvPath));
        }
    }

    [Fact]
    public async Task System_launcher_shadow_is_catalog_scoped_and_deleted_if_unchanged()
    {
        WriteSystemChromeLauncher();

        var setup = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);

        Assert.True(setup.Success);
        Assert.Equal(ExpectedPatchedChrome(SystemChromeContent), File.ReadAllText(UserChromePath));

        var removal = await BrowserAccessibilitySetupHelper.RemoveAsync(
            CancellationToken.None
        );

        Assert.True(removal.Success);
        Assert.False(File.Exists(UserChromePath));
    }

    [Fact]
    public async Task Foreign_user_launcher_is_backed_up_and_restored_with_exact_mode()
    {
        const string userLauncher =
            "[Desktop Entry]\nName=Custom Chrome\nExec=env WRAPPED=1 /opt/chrome %U\n";
        Directory.CreateDirectory(UserApps);
        await File.WriteAllTextAsync(UserChromePath, userLauncher);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(UserChromePath, Mode0600);
        }

        var setup = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);
        Assert.True(setup.Success);
        Assert.Equal(ExpectedPatchedChrome(userLauncher), File.ReadAllText(UserChromePath));

        var removal = await BrowserAccessibilitySetupHelper.RemoveAsync(
            CancellationToken.None
        );

        Assert.True(removal.Success);
        Assert.Equal(userLauncher, File.ReadAllText(UserChromePath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(Mode0600, File.GetUnixFileMode(UserChromePath));
        }
    }

    [Fact]
    public async Task Customized_launcher_shadow_is_preserved_with_transaction_state()
    {
        WriteSystemChromeLauncher();
        await new BrowserAccessibilitySetupHelper().SetUpAsync(CancellationToken.None);
        await File.AppendAllTextAsync(UserChromePath, "X-User-Customized=true\n");

        await BrowserAccessibilitySetupHelper.RemoveAsync(CancellationToken.None);

        Assert.Contains("X-User-Customized=true", File.ReadAllText(UserChromePath));
        Assert.True(
            File.Exists(
                Path.Join(StateRoot, $"browser-launcher-{ChromeDesktopId}", "state.json")
            )
        );
    }

    [Fact]
    public async Task Symlinked_launcher_is_refused_and_target_is_preserved()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WriteSystemChromeLauncher();
        Directory.CreateDirectory(UserApps);
        var target = Path.Join(_root, "launcher-target.desktop");
        await File.WriteAllTextAsync(target, "[Desktop Entry]\nName=Target\n");
        File.CreateSymbolicLink(UserChromePath, target);

        var result = await new BrowserAccessibilitySetupHelper().SetUpAsync(
            CancellationToken.None
        );

        // The symlinked launcher must not be claimed as patched, whatever the rest of the
        // run managed to do.
        Assert.DoesNotContain(ChromeDesktopId, result.Detail ?? string.Empty);
        Assert.Equal("[Desktop Entry]\nName=Target\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(UserChromePath).LinkTarget);
    }

    [Fact]
    public async Task Removal_does_not_sweep_unrelated_marker_bearing_desktop_files()
    {
        Directory.CreateDirectory(UserApps);
        var unrelated = Path.Join(UserApps, "unrelated.desktop");
        const string contents = $"{OwnershipComment}\n[Desktop Entry]\nExec=/usr/bin/unrelated\n";
        await File.WriteAllTextAsync(unrelated, contents);

        await BrowserAccessibilitySetupHelper.RemoveAsync(CancellationToken.None);

        Assert.Equal(contents, File.ReadAllText(unrelated));
    }

    [Fact]
    public async Task Exact_legacy_shadow_and_sidecar_are_adopted_and_restored()
    {
        const string original =
            "[Desktop Entry]\nName=Legacy custom Chrome\nExec=/opt/chrome %U\n";
        Directory.CreateDirectory(UserApps);
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyBackupPath)!);
        await File.WriteAllTextAsync(LegacyBackupPath, original);
        await File.WriteAllTextAsync(UserChromePath, ExpectedPatchedChrome(original));

        var setup = await new BrowserAccessibilitySetupHelper()
            .SetUpAsync(CancellationToken.None);
        var removal = await BrowserAccessibilitySetupHelper.RemoveAsync(
            CancellationToken.None
        );

        Assert.True(setup.Success);
        Assert.True(removal.Success);
        Assert.Equal(original, File.ReadAllText(UserChromePath));
    }

    private string ConfigHome => Path.Join(_root, "config");
    private string DataHome => Path.Join(_root, "data");
    private string StateRoot => Path.Join(_root, "state");
    private string SystemApps => Path.Join(_root, "system-applications");
    private string UserApps => Path.Join(DataHome, "applications");
    private string EnvPath =>
        Path.Join(ConfigHome, "environment.d", "typewhisper-accessibility.conf");
    private string UserChromePath => Path.Join(UserApps, ChromeDesktopId);
    private string LegacyBackupPath =>
        Path.Join(DataHome, "typewhisper", "launcher-backups", ChromeDesktopId);

    private const string SystemChromeContent =
        "[Desktop Entry]\nName=Google Chrome\nExec=/usr/bin/google-chrome %U\n";

    private void WriteSystemChromeLauncher()
    {
        Directory.CreateDirectory(SystemApps);
        File.WriteAllText(Path.Join(SystemApps, ChromeDesktopId), SystemChromeContent);
    }

    private static string ExpectedPatchedChrome(string source)
    {
        return OwnershipComment
               + "\n"
               + source.Replace(
                   " %U",
                   " --force-renderer-accessibility %U",
                   StringComparison.Ordinal
               );
    }
}
