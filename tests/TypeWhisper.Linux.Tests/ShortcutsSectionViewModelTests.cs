using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutsSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public ShortcutsSectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Linux.ShortcutsVmTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ApplyPromptPaletteHotkey_SavesConfiguredBinding()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Load();
        var hotkey = new HotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings);

        sut.PromptPaletteHotkeyText = "Ctrl+Shift+P";
        sut.ApplyPromptPaletteHotkeyCommand.Execute(null);

        Assert.Equal("Ctrl+Shift+P", settings.Current.PromptPaletteHotkey);
        Assert.Equal("Ctrl+Shift+P", sut.PromptPaletteHotkeyText);
    }

    [Fact]
    public void ApplyPromptPaletteHotkey_BlankInputClearsBinding()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Load();
        var hotkey = new HotkeyService();
        hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Shift+P");
        settings.Save(settings.Current with { PromptPaletteHotkey = "Ctrl+Shift+P" });

        var sut = new ShortcutsSectionViewModel(hotkey, settings)
        {
            PromptPaletteHotkeyText = ""
        };

        sut.ApplyPromptPaletteHotkeyCommand.Execute(null);

        Assert.Equal("", settings.Current.PromptPaletteHotkey);
        Assert.Equal("", sut.PromptPaletteHotkeyText);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }
}
