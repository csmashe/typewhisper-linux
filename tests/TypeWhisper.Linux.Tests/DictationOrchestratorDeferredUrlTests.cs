using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorDeferredUrlTests
{
    private static readonly ActiveWindowSnapshot s_tabA =
        new("firefox", "Alpha — Mozilla Firefox", "42", "firefox", "gnome-shell");

    [Fact]
    public void Same_window_and_title_keeps_the_deferred_url()
    {
        Assert.True(DictationOrchestrator.IsSameWindowAndTab(s_tabA, s_tabA with { }));
    }

    [Fact]
    public void Tab_switch_in_the_same_window_discards_the_deferred_url()
    {
        var tabB = s_tabA with { Title = "Beta — Mozilla Firefox" };

        Assert.False(DictationOrchestrator.IsSameWindowAndTab(s_tabA, tabB));
    }

    [Fact]
    public void Different_window_discards_the_deferred_url()
    {
        Assert.False(DictationOrchestrator.IsSameWindowAndTab(s_tabA, s_tabA with { WindowId = "43" }));
    }

    [Fact]
    public void Missing_snapshot_discards_the_deferred_url()
    {
        Assert.False(DictationOrchestrator.IsSameWindowAndTab(null, s_tabA));
        Assert.False(DictationOrchestrator.IsSameWindowAndTab(s_tabA, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_title_discards_the_deferred_url(string? title)
    {
        var untitled = s_tabA with { Title = title };

        Assert.False(DictationOrchestrator.IsSameWindowAndTab(untitled, untitled with { }));
        Assert.False(DictationOrchestrator.IsSameWindowAndTab(s_tabA, untitled));
        Assert.False(DictationOrchestrator.IsSameWindowAndTab(untitled, s_tabA));
    }
}
