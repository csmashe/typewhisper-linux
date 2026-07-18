using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
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

    [Fact]
    public async Task SetupAutomatically_PushToTalkWithPressOnlyWriter_ShowsUnsupportedAndDoesNotWrite()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(settings.Current with { Mode = RecordingMode.PushToTalk });
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var writer = new FakeDeShortcutWriter { SupportsPushToTalk = false };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        Assert.Contains(
            "Fake Desktop can't install a native shortcut for Push to talk mode",
            sut.IntegrationPreview
        );
        Assert.DoesNotContain("preview:", sut.IntegrationPreview);

        await sut.SetupAutomaticallyCommand.ExecuteAsync(null);

        Assert.Equal(0, writer.WriteCallCount);
        Assert.Contains(
            "Fake Desktop can't install a native shortcut for Push to talk mode",
            sut.IntegrationStatusMessage
        );
    }

    [Fact]
    public async Task SetupAutomatically_ToggleWithPressOnlyWriter_PreviewsAndWrites()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(settings.Current with { Mode = RecordingMode.Toggle });
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var writer = new FakeDeShortcutWriter { SupportsPushToTalk = false };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        Assert.StartsWith("preview:", sut.IntegrationPreview);
        Assert.DoesNotContain("can't install a native shortcut", sut.IntegrationPreview);

        await sut.SetupAutomaticallyCommand.ExecuteAsync(null);

        Assert.Equal(1, writer.WriteCallCount);
        Assert.NotNull(writer.LastWrittenSpec);
    }

    [Fact]
    public async Task SetupAutomatically_ImmediateLiveSuccessSuppressesOnlyDictationAndKeepsEvdevEnabled()
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Alt+P"));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter();
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.SetupAutomaticallyCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.Equal(1, writer.WriteCallCount);
        Assert.True(hotkey.NativeDictationBindingActive);
        Assert.True(sut.WaylandEvdevHotkeysEnabled);
        Assert.True(settings.Current.WaylandEvdevHotkeysEnabled);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
        Assert.Equal(KeyCode.VcP, backend.LastSet?.PromptPaletteKey);
        Assert.Contains("other app shortcuts active", sut.IntegrationStatusMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SetupAutomatically_DeferredSuccessDoesNotSuppressNow(
        bool requiresRestart,
        bool hasWarning
    )
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter
        {
            RequiresSessionRestartToApply = requiresRestart,
            WriteResult = new DeShortcutWriteResult(
                true,
                "Shortcut installed.",
                [],
                hasWarning ? "Live apply failed." : null
            )
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.SetupAutomaticallyCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.False(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcSpace, backend.LastSet?.DictationKey);
        Assert.Contains("later startup", sut.IntegrationStatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupAutomatically_FailureOrExceptionKeepsPreexistingSuppression(bool throws)
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.SetNativeDictationBindingActive(true);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter
        {
            WriteResult = new DeShortcutWriteResult(false, "Write failed.", []),
            WriteException = throws ? new InvalidOperationException("boom") : null
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.SetupAutomaticallyCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.True(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
    }

    [Fact]
    public async Task RemoveIntegration_ImmediateLiveSuccessRestoresAppDictationRoute()
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Alt+P"));
        hotkey.SetNativeDictationBindingActive(true);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter();
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RemoveIntegrationCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.Equal(1, writer.RemoveCallCount);
        Assert.False(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcSpace, backend.LastSet?.DictationKey);
        Assert.Equal(KeyCode.VcP, backend.LastSet?.PromptPaletteKey);
        Assert.Contains("manages its dictation hotkey again", sut.IntegrationStatusMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RemoveIntegration_DeferredSuccessKeepsPreexistingSuppression(
        bool requiresRestart,
        bool hasWarning
    )
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.SetNativeDictationBindingActive(true);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter
        {
            RequiresSessionRestartToApply = requiresRestart,
            RemoveResult = new DeShortcutWriteResult(
                true,
                "Shortcut removed.",
                [],
                hasWarning ? "Reload required." : null
            )
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RemoveIntegrationCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.True(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
        Assert.Contains("next startup", sut.IntegrationStatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveIntegration_FailureOrExceptionKeepsPreexistingSuppression(bool throws)
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.SetNativeDictationBindingActive(true);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var writer = new FakeDeShortcutWriter
        {
            RemoveResult = new DeShortcutWriteResult(false, "Remove failed.", []),
            RemoveException = throws ? new InvalidOperationException("boom") : null
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RemoveIntegrationCommand.ExecuteAsync(null);
        await backend.WaitUntilSettledAsync();

        Assert.True(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_UsesCurrentSpecAndSuppressesOnVerifiedInstall()
    {
        var settings = CreateToggleSettings("Alt+F8");
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var writer = new FakeDeShortcutWriter { IsInstalledResult = true };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);

        Assert.Equal(1, writer.IsInstalledCallCount);
        Assert.Equal("typewhisper.dictation.toggle", writer.LastInstalledSpec?.ShortcutId);
        Assert.Equal("Alt+F8", writer.LastInstalledSpec?.Trigger);
        Assert.True(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_ClearsSuppressionWhenSpecIsNotInstalled()
    {
        var settings = CreateToggleSettings();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.SetNativeDictationBindingActive(true);
        var writer = new FakeDeShortcutWriter { IsInstalledResult = false };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);

        Assert.Equal(1, writer.IsInstalledCallCount);
        Assert.False(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_ClearsSuppressionWithoutCurrentWriter()
    {
        var settings = CreateToggleSettings();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.SetNativeDictationBindingActive(true);
        var sut = new ShortcutsSectionViewModel(hotkey, settings, []);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);

        Assert.False(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_ClearsSuppressionForUnsupportedMode()
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(settings.Current with { Mode = RecordingMode.Hybrid });
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.SetNativeDictationBindingActive(true);
        var writer = new FakeDeShortcutWriter { IsInstalledResult = true };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);

        Assert.Equal(0, writer.IsInstalledCallCount);
        Assert.False(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_ProbeErrorFailsOpen()
    {
        var settings = CreateToggleSettings();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.SetNativeDictationBindingActive(true);
        var writer = new FakeDeShortcutWriter
        {
            IsInstalledException = new InvalidOperationException("boom")
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);

        Assert.Equal(1, writer.IsInstalledCallCount);
        Assert.False(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshNativeDictationBindingState_CancellationFailsOpenAndPropagates()
    {
        var settings = CreateToggleSettings();
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        hotkey.SetNativeDictationBindingActive(true);
        var writer = new FakeDeShortcutWriter
        {
            IsInstalledException = new OperationCanceledException()
        };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None)
        );

        Assert.False(hotkey.NativeDictationBindingActive);
    }

    [Fact]
    public async Task RefreshBeforeHotkeyInitialize_SuppressesFirstBackendSnapshot()
    {
        var settings = CreateToggleSettings();
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        var writer = new FakeDeShortcutWriter { IsInstalledResult = true };
        var sut = new ShortcutsSectionViewModel(hotkey, settings, [writer]);

        await sut.RefreshNativeDictationBindingStateAsync(CancellationToken.None);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        Assert.Equal(1, writer.IsInstalledCallCount);
        Assert.Equal(1, backend.RegisterCount);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
    }

    private SettingsService CreateToggleSettings(string toggleHotkey = "Ctrl+Shift+Space")
    {
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        settings.Load();
        settings.Save(
            settings.Current with
            {
                Mode = RecordingMode.Toggle,
                ToggleHotkey = toggleHotkey,
                WaylandEvdevHotkeysEnabled = true
            }
        );
        return settings;
    }
}
