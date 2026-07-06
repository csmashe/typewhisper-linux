using TypeWhisper.Core.Models;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AppInsertionStrategyRowTests
{
    private static readonly InsertionStrategyOption[] s_options =
    [
        new(TextInsertionStrategy.Auto, "Auto"),
        new(TextInsertionStrategy.ClipboardPaste, "Clipboard paste"),
        new(TextInsertionStrategy.DirectTyping, "Direct typing"),
        new(TextInsertionStrategy.CopyOnly, "Copy only")
    ];

    [Fact]
    public void SelectedStrategyOption_UpdatesStrategyAndNotifiesChange()
    {
        var changeCount = 0;
        var sut = new AppInsertionStrategyRow(
            "kitty",
            TextInsertionStrategy.Auto,
            s_options,
            () => changeCount++
        ) { SelectedStrategyOption = s_options.First(option =>
                option.Value == TextInsertionStrategy.DirectTyping
            )
        };

        Assert.Equal(TextInsertionStrategy.DirectTyping, sut.Strategy);
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void ProcessName_ChangeNotifiesChange()
    {
        var changeCount = 0;
        var sut = new AppInsertionStrategyRow(
            "kitty",
            TextInsertionStrategy.Auto,
            s_options,
            () => changeCount++
        ) { ProcessName = "firefox" };

        Assert.Equal("firefox", sut.ProcessName);
        Assert.Equal(1, changeCount);
    }
}