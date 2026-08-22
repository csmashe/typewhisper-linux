using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationSectionViewModelTests
{
    [Fact]
    public async Task ReadyEngineChange_RebuildsLanguagePickerAndPreservesInvalidSavedChoice()
    {
        var plugin = new ModelDependentLanguagePlugin();
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Default Mic", 1, isDefault: true)
        );
        using var context = new ViewModelTestContext(
            AppSettings.Default with { Language = "de", SelectedModelId = null },
            devices,
            [plugin]
        );

        Assert.Contains(context.Sut.LanguageChoices, option => option.Code == "it");

        context.Sut.SelectedModel = Assert.Single(
            context.Sut.ModelOptions,
            option => option.ModelId == plugin.FirstFullModelId
        );
        await WaitUntilAsync(() =>
            context.Sut.LanguageChoices.Select(option => option.Code)
                .SequenceEqual(["auto", "de", "fr"])
        );

        Assert.Equal("de", context.Sut.SelectedLanguageOption?.Code);
        Assert.False(context.Sut.LanguageSelectionRequired);
        context.Settings.Invocations.Clear();

        context.Sut.SelectedModel = Assert.Single(
            context.Sut.ModelOptions,
            option => option.ModelId == plugin.SecondFullModelId
        );
        await WaitUntilAsync(() =>
            context.Sut.LanguageChoices.Select(option => option.Code).SequenceEqual(["en"])
        );

        Assert.Equal("de", context.Settings.Object.Current.Language);
        Assert.Equal("de", context.Sut.Language);
        Assert.Null(context.Sut.SelectedLanguageOption);
        Assert.True(context.Sut.LanguageSelectionRequired);
        Assert.False(string.IsNullOrWhiteSpace(context.Sut.LanguageSelectionWarning));
        context.Settings.Verify(
            service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task AutoOnlyEngine_InvalidSavedChoice_ShowsSwitchToAutoWarning()
    {
        var plugin = new ModelDependentLanguagePlugin();
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Default Mic", 1, isDefault: true)
        );
        using var context = new ViewModelTestContext(
            AppSettings.Default with { Language = "de", SelectedModelId = null },
            devices,
            [plugin]
        );

        context.Sut.SelectedModel = Assert.Single(
            context.Sut.ModelOptions,
            option => option.ModelId == plugin.ThirdFullModelId
        );
        await WaitUntilAsync(() =>
            context.Sut.LanguageChoices.Select(option => option.Code).SequenceEqual(["auto"])
        );

        Assert.True(context.Sut.LanguageSelectionRequired);
        Assert.Equal(
            Loc.Instance["Dictation.LanguageSelectionRequiredAuto"],
            context.Sut.LanguageSelectionWarning
        );
        Assert.Equal("de", context.Settings.Object.Current.Language);

        context.Sut.SelectedModel = Assert.Single(
            context.Sut.ModelOptions,
            option => option.ModelId == plugin.SecondFullModelId
        );
        await WaitUntilAsync(() =>
            context.Sut.LanguageChoices.Select(option => option.Code).SequenceEqual(["en"])
        );

        Assert.Equal(
            Loc.Instance["Dictation.LanguageSelectionRequired"],
            context.Sut.LanguageSelectionWarning
        );
    }

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
            service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()),
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
            FakeAudioDeviceEnumerator devices,
            IReadOnlyList<ITranscriptionEngineRole>? engines = null
        )
        {
            Settings = TestPluginManagerFactory.CreateSettings(initialSettings);
            PluginManager = TestPluginManagerFactory.Create();
            if (engines is not null)
            {
                SetTranscriptionEngines(PluginManager, engines);
            }

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

    private static void SetTranscriptionEngines(
        PluginManager pluginManager,
        IReadOnlyList<ITranscriptionEngineRole> engines
    )
    {
        var field =
            typeof(PluginManager).GetField(
                "_transcriptionEngines",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingFieldException(
                typeof(PluginManager).FullName,
                "_transcriptionEngines"
            );
        field.SetValue(pluginManager, engines.ToList());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        // Generous ceiling for loaded CI machines; the loop exits as soon as the
        // condition holds, so the headroom costs nothing on the happy path.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(predicate(), "Condition was not reached before the test deadline.");
    }

    private sealed class ModelDependentLanguagePlugin
        : ITranscriptionEnginePlugin,
            ITranscriptionLanguageSelectionCapabilities
    {
        private const string FirstModelId = "multilingual";
        private const string SecondModelId = "english-only";
        private const string ThirdModelId = "auto-only";

        public string FirstFullModelId =>
            ModelManagerService.GetPluginModelId(PluginId, FirstModelId);
        public string SecondFullModelId =>
            ModelManagerService.GetPluginModelId(PluginId, SecondModelId);
        public string ThirdFullModelId =>
            ModelManagerService.GetPluginModelId(PluginId, ThirdModelId);
        public string PluginId => "com.test.language-picker";
        public string PluginName => "Language picker fake";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "language-picker";
        public string ProviderDisplayName => "Language picker";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
        [
            new(FirstModelId, "Multilingual"),
            new(SecondModelId, "English only"),
            new(ThirdModelId, "Auto only"),
        ];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public LanguageSelectionSupport AutomaticDetectionSupport =>
            SelectedModelId == SecondModelId
                ? LanguageSelectionSupport.Unsupported
                : LanguageSelectionSupport.Supported;
        public LanguageSelectionSupport ExplicitSelectionSupport =>
            SelectedModelId == ThirdModelId
                ? LanguageSelectionSupport.Unsupported
                : LanguageSelectionSupport.Supported;
        public IReadOnlyList<string> SupportedLanguages =>
            SelectedModelId == SecondModelId ? ["en"] : ["de", "fr"];

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;

        public void SelectModel(string modelId)
        {
            SelectedModelId = modelId;
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
