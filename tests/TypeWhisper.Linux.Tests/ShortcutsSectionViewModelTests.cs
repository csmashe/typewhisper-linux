using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutsSectionViewModelTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.Linux.ShortcutsSectionViewModelTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void ApplyPromptPaletteHotkey_SavesConfiguredBinding()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings) { PromptPaletteHotkeyText = "Ctrl+Shift+P" };

        sut.ApplyPromptPaletteHotkeyCommand.Execute(null);

        Assert.Equal("Ctrl+Shift+P", settings.Current.PromptPaletteHotkey);
        Assert.Equal("Ctrl+Shift+P", sut.PromptPaletteHotkeyText);
    }

    [Fact]
    public void ApplyPromptPaletteHotkey_BlankInputClearsBinding()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Shift+P");
        settings.Save(settings.Current with { PromptPaletteHotkey = "Ctrl+Shift+P" });

        var sut = new ShortcutsSectionViewModel(hotkey, settings) { PromptPaletteHotkeyText = "" };

        sut.ApplyPromptPaletteHotkeyCommand.Execute(null);

        Assert.Equal("", settings.Current.PromptPaletteHotkey);
        Assert.Equal("", sut.PromptPaletteHotkeyText);
    }

    [Fact]
    public void ApplyTransformSelectionHotkey_SavesConfiguredBinding()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings) { TransformSelectionHotkeyText = "Ctrl+Shift+T" };

        sut.ApplyTransformSelectionHotkeyCommand.Execute(null);

        Assert.Equal("Ctrl+Shift+T", settings.Current.TransformSelectionHotkey);
        Assert.Equal("Ctrl+Shift+T", sut.TransformSelectionHotkeyText);
    }

    [Fact]
    public void ApplyTransformSelectionHotkey_BlankInputClearsBinding()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
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
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
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

    [Fact]
    public void WaylandEvdevHotkeysEnabled_PersistsToSettings()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings);

        Assert.True(sut.WaylandEvdevHotkeysEnabled);
        sut.WaylandEvdevHotkeysEnabled = false;

        Assert.False(settings.Current.WaylandEvdevHotkeysEnabled);
    }

    [Fact]
    public void ShowCapabilityMismatch_FalseInDefaultState()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings);

        // No backend initialized yet, so no capability mismatch to surface.
        Assert.False(sut.ShowCapabilityMismatch);
    }

    [Fact]
    public void ActiveBackendId_DefaultsToNotInitialized()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var sut = new ShortcutsSectionViewModel(hotkey, settings);

        // Initialize hasn't been called → coordinator hasn't resolved a
        // backend yet → status panel shows the placeholder.
        Assert.Equal("(not initialized)", sut.ActiveBackendId);
        Assert.Equal("(not initialized)", sut.ActiveBackendDisplayName);
    }
}
