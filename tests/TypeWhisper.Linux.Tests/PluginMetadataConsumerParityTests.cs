using Moq;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PluginMetadataConsumerParityTests
{
    [Theory]
    [InlineData(PluginNetworkAccess.Local, "Local", true)]
    [InlineData(PluginNetworkAccess.Network, "Cloud", false)]
    [InlineData(PluginNetworkAccess.Mixed, "Mixed", false)]
    [InlineData(PluginNetworkAccess.UserControlled, "User controlled", false)]
    public void Descriptor_WizardSettingsAndRanLocallyProjectionAgree(
        PluginNetworkAccess networkAccess,
        string expectedBadge,
        bool expectedRanLocally
    )
    {
        var plugin = new FakePlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            Path.GetTempPath(),
            plugin.PluginId,
            plugin,
            networkAccess,
            [PluginCategory.Tts]
        );
        var settings = TestPluginManagerFactory.CreateSettings(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create(
            loadedPlugins: [loaded]
        );
        using var models = new ModelManagerService(pluginManager, settings.Object);
        using var hotkey = new HotkeyService(
            new BackendSelector(static () => new TestShortcutBackend())
        );
        using var audio = new AudioRecordingService(_ => { }, () => 0, () => { });
        var textInsertion = new TextInsertionService(
            new NoOpTextInsertionPlatform()
        );
        var dictionary = new Mock<IDictionaryService>();
        var wizard = new WelcomeWizardViewModel(
            models,
            pluginManager,
            hotkey,
            audio,
            CreateCommandsWithoutHostProbes(),
            textInsertion,
            [],
            dictionary.Object,
            settings.Object,
            availableMics: []
        );

        try
        {
            var wizardRow = Assert.Single(wizard.ExtensionPlugins);
            var settingsViewModel = new PluginsSectionViewModel(pluginManager);
            var settingsGroup = Assert.Single(settingsViewModel.PluginGroups);
            var settingsRow = Assert.Single(settingsGroup.Plugins);

            Assert.Equal("Text-to-Speech", settingsGroup.Title);
            Assert.Equal(networkAccess, loaded.Metadata.NetworkAccess);
            Assert.Equal(networkAccess, wizardRow.NetworkAccess);
            Assert.Equal(networkAccess, settingsRow.NetworkAccess);
            Assert.Equal(expectedBadge, wizardRow.LocationBadge);
            Assert.Equal(expectedBadge, settingsRow.LocationBadge);
            Assert.Equal(expectedRanLocally, loaded.Metadata.RanLocally);
            Assert.Equal(expectedRanLocally, wizardRow.RanLocally);
            Assert.Equal(expectedRanLocally, settingsRow.RanLocally);
        }
        finally
        {
            wizard.Cleanup();
        }
    }

    private static SystemCommandAvailabilityService CreateCommandsWithoutHostProbes()
    {
        var commands = (SystemCommandAvailabilityService)
            RuntimeHelpers.GetUninitializedObject(
                typeof(SystemCommandAvailabilityService)
            );
        commands.RaiseSnapshotChangedForTests(
            new LinuxCapabilitySnapshot(
                "Unknown",
                false,
                "none",
                false,
                false,
                false,
                false,
                null,
                false,
                false,
                false,
                false,
                false
            )
        );
        return commands;
    }

    private sealed class FakePlugin : ITypeWhisperPlugin
    {
        public string PluginId => "com.test.metadata-parity";
        public string PluginName => "Metadata parity";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class NoOpTextInsertionPlatform : ITextInsertionPlatform
    {
        public bool IsClipboardSetAvailable => false;
        public bool IsPasteAvailable => false;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => false;
        public InsertionFailureReason LastFailureReason =>
            InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;

        public Task<string?> TryGetClipboardTextAsync() =>
            Task.FromResult<string?>(null);

        public Task<bool> SetClipboardTextAsync(string text) =>
            Task.FromResult(false);

        public Task<bool> ClipboardHasNonTextFormatsAsync() =>
            Task.FromResult(false);

        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;
        public string? GetActiveWindowId() => null;

        public Task<bool> ActivateWindowAsync(string windowId) =>
            Task.FromResult(false);

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false) =>
            Task.FromResult(false);

        public Task<bool> TypeTextAsync(string text) =>
            Task.FromResult(false);

        public Task<bool> SendCopyAsync(bool useTerminalShortcut) =>
            Task.FromResult(false);

        public Task<bool> SendEnterAsync() => Task.FromResult(false);
    }
}
