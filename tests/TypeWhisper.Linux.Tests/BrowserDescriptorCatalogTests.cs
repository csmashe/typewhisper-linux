using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class BrowserDescriptorCatalogTests
{
    [Fact]
    public void Catalog_alias_sets_are_case_insensitively_unique()
    {
        AssertUnique(BrowserDescriptorCatalog.All.Select(descriptor => descriptor.Id));
        AssertUnique(
            BrowserDescriptorCatalog.All.SelectMany(descriptor => descriptor.ProcessAliases)
        );
        AssertUnique(
            BrowserDescriptorCatalog.All.SelectMany(
                descriptor => descriptor.WindowIdentityAliases
            )
        );
        AssertUnique(
            BrowserDescriptorCatalog.All.SelectMany(descriptor => descriptor.TitleAliases)
        );
        AssertUnique(
            BrowserDescriptorCatalog.All.SelectMany(descriptor => descriptor.DesktopIds)
        );
    }

    [Fact]
    public void Catalog_capabilities_have_complete_projection_metadata()
    {
        foreach (var descriptor in BrowserDescriptorCatalog.All)
        {
            if (descriptor.HasCapability(BrowserCapabilities.ActiveWindowDetection))
            {
                Assert.True(
                    descriptor.ProcessAliases.Count > 0
                    || descriptor.WindowIdentityAliases.Count > 0
                );
            }

            Assert.Equal(
                descriptor.HasCapability(BrowserCapabilities.LauncherSetup),
                descriptor.LauncherPatchMode != BrowserLauncherPatchMode.None
                && descriptor.DesktopIds.Count > 0
            );
            Assert.Equal(
                descriptor.HasCapability(BrowserCapabilities.FirefoxProfileSetup),
                descriptor.ProfileRoots.Count > 0
            );
            Assert.Equal(
                descriptor.HasCapability(BrowserCapabilities.TitleOnlyDetection),
                descriptor.TitleAliases.Count > 0
            );
        }
    }

    [Fact]
    public void Every_process_alias_resolves_to_its_descriptor_through_Active()
    {
        foreach (var descriptor in BrowserDescriptorCatalog.All)
        {
            foreach (var alias in descriptor.ProcessAliases)
            {
                var snapshot = Snapshot(processName: alias);

                Assert.Same(
                    descriptor,
                    ActiveWindowService.ResolveBrowserDescriptor(snapshot)
                );
            }
        }
    }

    [Fact]
    public void Every_raw_window_alias_resolves_to_its_descriptor_through_Active()
    {
        foreach (var descriptor in BrowserDescriptorCatalog.All)
        {
            foreach (var alias in descriptor.WindowIdentityAliases)
            {
                var snapshot = Snapshot(appId: alias);

                Assert.Same(
                    descriptor,
                    ActiveWindowService.ResolveBrowserDescriptor(snapshot)
                );
            }
        }
    }

    [Fact]
    public void Only_opted_in_title_aliases_resolve_through_Active()
    {
        foreach (var descriptor in BrowserDescriptorCatalog.All)
        {
            foreach (var alias in descriptor.TitleAliases)
            {
                Assert.Same(
                    descriptor,
                    ActiveWindowService.ResolveBrowserDescriptor(
                        Snapshot(title: $"Inbox — {alias}")
                    )
                );
            }

            // No window identity alias means there is no non-opted-in title to reject.
            if (
                descriptor.HasCapability(BrowserCapabilities.TitleOnlyDetection)
                || descriptor.WindowIdentityAliases.Count == 0
            )
            {
                continue;
            }

            Assert.Null(
                ActiveWindowService.ResolveBrowserDescriptor(
                    Snapshot(title: $"Example — {descriptor.WindowIdentityAliases[0]}")
                )
            );
        }
    }

    [Fact]
    public void Desktop_ids_are_not_automatically_process_aliases()
    {
        foreach (var desktopId in BrowserDescriptorCatalog.All.SelectMany(
                     descriptor => descriptor.DesktopIds
                 ))
        {
            Assert.False(ActiveWindowService.IsSupportedBrowserProcess(desktopId));
        }
    }

    [Fact]
    public void Desktop_ids_resolve_as_app_ids_for_GNOME_shell_snapshots()
    {
        foreach (var descriptor in BrowserDescriptorCatalog.All)
        {
            foreach (var desktopId in descriptor.DesktopIds)
            {
                Assert.Same(
                    descriptor,
                    ActiveWindowService.ResolveBrowserDescriptor(
                        Snapshot(appId: desktopId)
                    )
                );
            }
        }
    }

    private static ActiveWindowSnapshot Snapshot(
        string? processName = null,
        string? appId = null,
        string? title = null
    )
    {
        return new ActiveWindowSnapshot(processName, title, null, appId, "test");
    }

    private static void AssertUnique(IEnumerable<string> values)
    {
        var items = values.ToList();
        Assert.Equal(
            items.Count,
            items.Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );
        Assert.DoesNotContain(items, string.IsNullOrWhiteSpace);
    }
}
