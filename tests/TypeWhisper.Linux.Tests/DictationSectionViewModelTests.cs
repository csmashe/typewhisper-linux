using Moq;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationSectionViewModelTests
{
    [Fact]
    public void RefreshDevices_WhenPinnedIdentityDisappears_ClearsRuntimeSelectionAndPreservesPreference()
    {
        const int pinnedIndex = 4;
        const int defaultIndex = 9;
        const string pinnedId = "Wanted Mic|1";
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(pinnedIndex, "Wanted Mic", 1, isDefault: false),
            new FakeDevice(defaultIndex, "Current Default", 1, isDefault: true)
        );
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = pinnedIndex,
            SelectedMicrophoneDeviceId = pinnedId,
        };
        using var context = new ViewModelTestContext(originalSettings, devices);
        Assert.Equal(pinnedIndex, context.Audio.SelectedDeviceIndex);

        devices.SetDevices(
            new FakeDevice(pinnedIndex, "Replacement Mic", 1, isDefault: false),
            new FakeDevice(defaultIndex, "Current Default", 1, isDefault: true)
        );

        context.Sut.RefreshDevicesCommand.Execute(null);

        Assert.Null(context.Sut.SelectedDevice);
        Assert.Null(context.Audio.SelectedDeviceIndex);
        Assert.False(context.Audio.FollowSystemDefault);
        Assert.Equal(pinnedIndex, context.Settings.Object.Current.SelectedMicrophoneDevice);
        Assert.Equal(pinnedId, context.Settings.Object.Current.SelectedMicrophoneDeviceId);
        context.Settings.Verify(
            service => service.Save(It.IsAny<AppSettings>()),
            Times.Never
        );

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            context.Audio.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal([defaultIndex], context.OpenedDeviceIndices);

        context.Audio.StopRecording(session);
    }

    [Fact]
    public void RefreshDevices_WhenPinnedIdentityMoves_KeepsRuntimeTrackingIt()
    {
        const int originalIndex = 4;
        const int movedIndex = 7;
        const int defaultIndex = 9;
        const string pinnedId = "Wanted Mic|1";
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(originalIndex, "Wanted Mic", 1, isDefault: false),
            new FakeDevice(defaultIndex, "Current Default", 1, isDefault: true)
        );
        using var context = new ViewModelTestContext(
            AppSettings.Default with
            {
                SelectedMicrophoneDevice = originalIndex,
                SelectedMicrophoneDeviceId = pinnedId,
            },
            devices
        );

        devices.SetDevices(
            new FakeDevice(originalIndex, "Replacement Mic", 1, isDefault: false),
            new FakeDevice(movedIndex, "Wanted Mic", 1, isDefault: false),
            new FakeDevice(defaultIndex, "Current Default", 1, isDefault: true)
        );

        context.Sut.RefreshDevicesCommand.Execute(null);

        Assert.Equal(pinnedId, context.Sut.SelectedDevice?.PersistentId);
        Assert.Equal(movedIndex, context.Sut.SelectedDevice?.Index);
        Assert.Equal(movedIndex, context.Audio.SelectedDeviceIndex);
        Assert.False(context.Audio.FollowSystemDefault);

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            context.Audio.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal([movedIndex], context.OpenedDeviceIndices);

        context.Audio.StopRecording(session);
    }

    private sealed class ViewModelTestContext : IDisposable
    {
        public ViewModelTestContext(
            AppSettings initialSettings,
            FakeAudioDeviceEnumerator devices
        )
        {
            Settings = TestPluginManagerFactory.CreateSettings(initialSettings);
            PluginManager = TestPluginManagerFactory.Create();
            var commands = new SystemCommandAvailabilityService();
            Models = new ModelManagerService(PluginManager, Settings.Object, commands);
            Audio = new AudioRecordingService(
                devices.GetDevices,
                OpenedDeviceIndices.Add,
                () => devices.GetDevices().Single(device => device.IsDefault).Index,
                static () => { }
            );
            // ReSharper disable once InconsistentNaming -- "a11y" is the standard numeronym for accessibility.
            var a11yBus = new Mock<IAccessibilityBusActivation>();
            a11yBus
                .Setup(bus => bus.IsActivatedAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var dictation = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(
                typeof(DictationOrchestrator)
            );
            Sut = new DictationSectionViewModel(
                dictation,
                Models,
                Audio,
                Settings.Object,
                PluginManager,
                commands,
                new CudaLibraryPathSetupService(),
                a11yBus.Object,
                devices.GetDevices
            );
            Settings.Invocations.Clear();
        }

        public DictationSectionViewModel Sut { get; }
        public Mock<ISettingsService> Settings { get; }
        private PluginManager PluginManager { get; }
        private ModelManagerService Models { get; }
        public AudioRecordingService Audio { get; }
        public List<int> OpenedDeviceIndices { get; } = [];

        public void Dispose()
        {
            Audio.Dispose();
            Models.Dispose();
            PluginManager.Dispose();
        }
    }
}
