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

    [Fact]
    public void ApplyTransformSelectionHotkey_SavesConfiguredBinding()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Load();
        var hotkey = new HotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings);

        sut.TransformSelectionHotkeyText = "Ctrl+Shift+T";
        sut.ApplyTransformSelectionHotkeyCommand.Execute(null);

        Assert.Equal("Ctrl+Shift+T", settings.Current.TransformSelectionHotkey);
        Assert.Equal("Ctrl+Shift+T", sut.TransformSelectionHotkeyText);
    }

    [Fact]
    public void ApplyTransformSelectionHotkey_BlankInputClearsBinding()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Load();
        var hotkey = new HotkeyService();
        hotkey.TrySetTransformSelectionHotkeyFromString("Ctrl+Shift+T");
        settings.Save(settings.Current with { TransformSelectionHotkey = "Ctrl+Shift+T" });

        var sut = new ShortcutsSectionViewModel(hotkey, settings)
        {
            TransformSelectionHotkeyText = ""
        };

        sut.ApplyTransformSelectionHotkeyCommand.Execute(null);

        Assert.Equal("", settings.Current.TransformSelectionHotkey);
        Assert.Equal("", sut.TransformSelectionHotkeyText);
    }

    [Fact]
    public void ApplyTransformSelectionHotkey_RejectsCollisionWithPromptPalette()
    {
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        settings.Load();
        var hotkey = new HotkeyService();
        hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Shift+P");
        settings.Save(settings.Current with { PromptPaletteHotkey = "Ctrl+Shift+P" });

        var sut = new ShortcutsSectionViewModel(hotkey, settings)
        {
            TransformSelectionHotkeyText = "Ctrl+Shift+P"
        };

        sut.ApplyTransformSelectionHotkeyCommand.Execute(null);

        Assert.Equal("", settings.Current.TransformSelectionHotkey);
        Assert.Contains("collides", sut.StatusMessage);
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
