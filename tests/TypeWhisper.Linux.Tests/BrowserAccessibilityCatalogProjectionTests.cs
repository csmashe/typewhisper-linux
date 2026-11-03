using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BrowserAccessibilityCatalogProjectionTests
{
    [Fact]
    public void Firefox_setup_launcher_projection_exactly_matches_catalog()
    {
        AssertLauncherProjection(BrowserLauncherPatchMode.FirefoxEnvironment);
    }

    [Fact]
    public void Chromium_setup_launcher_projection_exactly_matches_catalog()
    {
        AssertLauncherProjection(
            BrowserLauncherPatchMode.ChromiumRendererAccessibility
        );
    }

    private static void AssertLauncherProjection(BrowserLauncherPatchMode patchMode)
    {
        var expected = BrowserDescriptorCatalog.All
            .Where(descriptor =>
                descriptor.HasCapability(BrowserCapabilities.LauncherSetup)
                && descriptor.LauncherPatchMode == patchMode
            )
            .SelectMany(descriptor => descriptor.DesktopIds)
            .ToList();

        Assert.Equal(
            expected,
            BrowserAccessibilitySetupHelper.GetLauncherNames(patchMode)
        );
    }

    [Fact]
    public void Setup_profile_root_projection_exactly_matches_catalog()
    {
        const string customXdg = "/tmp/typewhisper-browser-catalog-xdg";
        var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", customXdg);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expected = BrowserDescriptorCatalog.All
                .Where(descriptor =>
                    descriptor.HasCapability(BrowserCapabilities.FirefoxProfileSetup)
                )
                .SelectMany(descriptor => descriptor.ProfileRoots)
                .Select(root =>
                    Path.Join(
                        root.Base == ProfileRootBase.Home ? home : customXdg,
                        root.RelativePath
                    )
                )
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                expected,
                BrowserAccessibilitySetupHelper.GetFirefoxProfileRoots()
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
        }
    }

    [Fact]
    public void Waterfox_launcher_setup_projects_native_and_flatpak_ids()
    {
        var launchers = BrowserAccessibilitySetupHelper.GetLauncherNames(
            BrowserLauncherPatchMode.FirefoxEnvironment
        );

        Assert.Contains("waterfox.desktop", launchers);
        Assert.Contains("net.waterfox.waterfox.desktop", launchers);
    }

    [Fact]
    public void LibreWolf_launcher_setup_projects_native_and_flatpak_ids()
    {
        var launchers = BrowserAccessibilitySetupHelper.GetLauncherNames(
            BrowserLauncherPatchMode.FirefoxEnvironment
        );

        Assert.Contains("librewolf.desktop", launchers);
        Assert.Contains("io.gitlab.librewolf-community.desktop", launchers);
    }

    [Fact]
    public void Waterfox_profile_setup_projects_native_and_flatpak_roots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = BrowserAccessibilitySetupHelper.GetFirefoxProfileRoots();

        Assert.Contains(Path.Join(home, ".waterfox"), roots);
        Assert.Contains(
            Path.Join(home, ".var/app/net.waterfox.waterfox/.waterfox"),
            roots
        );
    }

    [Fact]
    public void LibreWolf_profile_setup_projects_documented_XDG_root()
    {
        const string customXdg = "/tmp/typewhisper-librewolf-xdg";
        var originalXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", customXdg);

            Assert.Contains(
                Path.Join(customXdg, "librewolf/librewolf"),
                BrowserAccessibilitySetupHelper.GetFirefoxProfileRoots()
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalXdg);
        }
    }

    [Theory]
    [InlineData("com.google.Chrome.desktop")]
    [InlineData("org.chromium.Chromium.desktop")]
    [InlineData("com.microsoft.Edge.desktop")]
    [InlineData("com.brave.Browser.desktop")]
    [InlineData("com.vivaldi.Vivaldi.desktop")]
    [InlineData("com.opera.Opera.desktop")]
    public void Chromium_family_setup_projects_each_system_Flatpak_id(
        string desktopId
    )
    {
        Assert.Contains(
            desktopId,
            BrowserAccessibilitySetupHelper.GetLauncherNames(
                BrowserLauncherPatchMode.ChromiumRendererAccessibility
            )
        );
    }

    [Fact]
    public void Chromium_family_has_no_Firefox_profile_setup_projection()
    {
        Assert.All(
            BrowserDescriptorCatalog.All.Where(descriptor =>
                descriptor.EngineFamily == BrowserEngineFamily.Chromium
            ),
            descriptor =>
            {
                Assert.False(
                    descriptor.HasCapability(
                        BrowserCapabilities.FirefoxProfileSetup
                    )
                );
                Assert.Empty(descriptor.ProfileRoots);
            }
        );
    }
}
