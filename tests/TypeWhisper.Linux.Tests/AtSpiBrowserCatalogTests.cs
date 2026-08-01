using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AtSpiBrowserCatalogTests
{
    [Fact]
    public void Every_AtSpi_process_alias_passes_the_catalog_gate()
    {
        foreach (
            var descriptor in BrowserDescriptorCatalog.All.Where(descriptor =>
                descriptor.HasCapability(BrowserCapabilities.AtSpiExtraction)
            )
        )
        {
            foreach (var alias in descriptor.ProcessAliases)
            {
                Assert.Same(
                    descriptor,
                    AtSpiUrlExtractor.ResolveBrowserDescriptor(alias, null, null)
                );
            }
        }
    }

    [Fact]
    public void Every_AtSpi_application_alias_matches_only_its_own_descriptor()
    {
        var descriptors = BrowserDescriptorCatalog.All
            .Where(descriptor =>
                descriptor.HasCapability(BrowserCapabilities.AtSpiExtraction)
            )
            .ToList();

        foreach (var applicationDescriptor in descriptors)
        {
            foreach (var alias in applicationDescriptor.WindowIdentityAliases)
            {
                foreach (var focusedDescriptor in descriptors)
                {
                    Assert.Equal(
                        applicationDescriptor.Id == focusedDescriptor.Id,
                        AtSpiUrlExtractor.IsMatchingApp(alias, focusedDescriptor)
                    );
                }
            }
        }
    }

    [Fact]
    public void LibreWolf_process_and_flatpak_app_id_pass_the_AtSpi_gate()
    {
        var byProcess = AtSpiUrlExtractor.ResolveBrowserDescriptor(
            "librewolf",
            null,
            null
        );
        var byAppId = AtSpiUrlExtractor.ResolveBrowserDescriptor(
            null,
            "io.gitlab.librewolf-community",
            null
        );

        Assert.Equal("librewolf", byProcess?.Id);
        Assert.Equal("librewolf", byAppId?.Id);
    }

    [Fact]
    public void Waterfox_process_and_flatpak_app_id_pass_the_AtSpi_gate()
    {
        var byProcess = AtSpiUrlExtractor.ResolveBrowserDescriptor(
            "waterfox",
            null,
            null
        );
        var byAppId = AtSpiUrlExtractor.ResolveBrowserDescriptor(
            null,
            "net.waterfox.waterfox",
            null
        );

        Assert.Equal("waterfox", byProcess?.Id);
        Assert.Equal("waterfox", byAppId?.Id);
    }

    [Theory]
    [InlineData("edge")]
    [InlineData("msedge")]
    public void Edge_process_aliases_pass_the_AtSpi_gate(string alias)
    {
        Assert.Equal(
            "edge",
            AtSpiUrlExtractor.ResolveBrowserDescriptor(alias, null, null)?.Id
        );
    }

    [Fact]
    public void A_browser_looking_title_never_opens_the_gate_for_a_known_process()
    {
        Assert.Null(
            AtSpiUrlExtractor.ResolveBrowserDescriptor(
                "code",
                null,
                "AtSpiUrlExtractor.cs — Zen Browser"
            )
        );
        Assert.Equal(
            "zen",
            AtSpiUrlExtractor.ResolveBrowserDescriptor(
                null,
                null,
                "Inbox — Zen Browser"
            )?.Id
        );
    }

    [Fact]
    public void Firefox_focused_rejects_LibreWolf_and_Waterfox_applications()
    {
        var firefox = Descriptor("firefox");

        Assert.True(AtSpiUrlExtractor.IsMatchingApp("Firefox", firefox));
        Assert.False(AtSpiUrlExtractor.IsMatchingApp("LibreWolf", firefox));
        Assert.False(AtSpiUrlExtractor.IsMatchingApp("Waterfox", firefox));
    }

    [Fact]
    public void LibreWolf_and_Waterfox_focused_reject_Firefox_application()
    {
        Assert.False(
            AtSpiUrlExtractor.IsMatchingApp("Firefox", Descriptor("librewolf"))
        );
        Assert.False(
            AtSpiUrlExtractor.IsMatchingApp("Firefox", Descriptor("waterfox"))
        );
    }

    [Fact]
    public void Chrome_and_Brave_reject_each_others_AtSpi_applications()
    {
        var chrome = Descriptor("chrome");
        var brave = Descriptor("brave");

        Assert.True(AtSpiUrlExtractor.IsMatchingApp("Google Chrome", chrome));
        Assert.True(AtSpiUrlExtractor.IsMatchingApp("Brave", brave));
        Assert.False(AtSpiUrlExtractor.IsMatchingApp("Brave", chrome));
        Assert.False(AtSpiUrlExtractor.IsMatchingApp("Google Chrome", brave));
    }

    [Fact]
    public void AtSpi_application_matching_is_exact_not_substring_based()
    {
        Assert.False(
            AtSpiUrlExtractor.IsMatchingApp(
                "Mozilla Firefox Nightly",
                Descriptor("firefox")
            )
        );
        Assert.False(
            AtSpiUrlExtractor.IsMatchingApp("Brave Beta", Descriptor("brave"))
        );
    }

    private static BrowserDescriptor Descriptor(string id)
    {
        return Assert.Single(
            BrowserDescriptorCatalog.All,
            descriptor => descriptor.Id == id
        );
    }
}
