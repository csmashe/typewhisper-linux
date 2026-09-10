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
    [Theory]
    [InlineData("-1", -1, "-1")]
    [InlineData("0", 1, "1")]
    [InlineData("900", 365, "365")]
    [InlineData("7", 7, "7")]
    // Unparseable text is left in the box so the user can keep typing.
    [InlineData("invalid", 30, "invalid")]
    public void RecoveryRetention_ParsesAndClamps(string input, int expected, string expectedText)
    {
        using var context = new ViewModelTestContext(AppSettings.Default,
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));
        Assert.Equal("30", context.Sut.DictationRecoveryRetentionDays);
        context.Sut.DictationRecoveryRetentionDays = input;
        Assert.Equal(expected, context.Settings.Object.Current.DictationRecoveryRetentionDays);
        Assert.Equal(expectedText, context.Sut.DictationRecoveryRetentionDays);
    }

    [Fact]
    public void AdditionalLanguages_LoadFromSettingsHintsTail()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "en", "fr"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        Assert.Equal("de", context.Sut.Language);
        Assert.Equal(["en", "fr"], context.Sut.AdditionalLanguages.Select(option => option.Code));
        Assert.True(context.Sut.IsAdditionalLanguagesVisible);
        Assert.Equal(["de", "en", "fr"], context.Settings.Object.Current.LanguageHints);
    }

    [Fact]
    public void AdditionalLanguages_AddReorderRemove_PersistOrderedHints()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.SelectedAdditionalLanguage = context.Sut.AvailableAdditionalLanguages.Single(option => option.Code == "en");
        context.Sut.AddAdditionalLanguageCommand.Execute(null);
        Assert.Null(context.Sut.SelectedAdditionalLanguage);
        context.Sut.SelectedAdditionalLanguage = context.Sut.AvailableAdditionalLanguages.Single(option => option.Code == "fr");
        context.Sut.AddAdditionalLanguageCommand.Execute(null);
        var french = context.Sut.AdditionalLanguages.Single(option => option.Code == "fr");
        context.Sut.MoveAdditionalLanguageEarlierCommand.Execute(french);
        Assert.Equal(["de", "fr", "en"], context.Settings.Object.Current.LanguageHints);
        Assert.Equal("de", context.Settings.Object.Current.Language);
        context.Sut.MoveAdditionalLanguageEarlierCommand.Execute(french);
        Assert.Equal(["de", "fr", "en"], context.Settings.Object.Current.LanguageHints);
        context.Sut.MoveAdditionalLanguageLaterCommand.Execute(french);
        context.Sut.MoveAdditionalLanguageLaterCommand.Execute(french);
        Assert.Equal(["de", "en", "fr"], context.Settings.Object.Current.LanguageHints);
        context.Sut.RemoveAdditionalLanguageCommand.Execute(french);
        Assert.Equal(["de", "en"], context.Settings.Object.Current.LanguageHints);
    }

    [Fact]
    public void AdditionalLanguages_AddDuplicate_IsIgnored()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "en"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.SelectedAdditionalLanguage = new SpokenLanguageOption("EN", "English");
        context.Sut.AddAdditionalLanguageCommand.Execute(null);
        Assert.Equal("en", Assert.Single(context.Sut.AdditionalLanguages).Code);
        Assert.Equal(["de", "en"], context.Settings.Object.Current.LanguageHints);
        Assert.Null(context.Sut.SelectedAdditionalLanguage);
    }

    [Fact]
    public void AvailableAdditionalLanguages_ExcludesAutoPrimaryAndChips()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "en"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        Assert.Equal(
            context.Sut.LanguageChoices.Where(option => option.Code is not ("auto" or "de" or "en")),
            context.Sut.AvailableAdditionalLanguages);
        context.Sut.SelectedAdditionalLanguage = context.Sut.AvailableAdditionalLanguages.Single(option => option.Code == "fr");
        context.Sut.AddAdditionalLanguageCommand.Execute(null);
        Assert.DoesNotContain(context.Sut.AvailableAdditionalLanguages, option => option.Code == "fr");
        context.Sut.RemoveAdditionalLanguageCommand.Execute(context.Sut.AdditionalLanguages.Single(option => option.Code == "fr"));
        Assert.Contains(context.Sut.AvailableAdditionalLanguages, option => option.Code == "fr");
    }

    [Fact]
    public void Language_SetToAuto_ClearsAdditionalLanguages()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "en"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.Language = "auto";
        Assert.Empty(context.Sut.AdditionalLanguages);
        Assert.Empty(context.Settings.Object.Current.LanguageHints);
        Assert.Equal("auto", context.Settings.Object.Current.Language);
        Assert.False(context.Sut.IsAdditionalLanguagesVisible);
        Assert.False(context.Sut.HasAdditionalLanguages);
    }

    [Fact]
    public void Language_SetToChipCode_DropsThatChip()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "en"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.Language = "en";
        Assert.Empty(context.Sut.AdditionalLanguages);
        Assert.Equal(["en"], context.Settings.Object.Current.LanguageHints);
        Assert.Equal("en", context.Settings.Object.Current.Language);
    }

    [Fact]
    public void AdditionalLanguages_UnknownCode_IsKeptWithCodeAsName()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default.WithLanguageHints(["de", "xx"]),
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        var chip = Assert.Single(context.Sut.AdditionalLanguages);
        Assert.Equal("xx", chip.Code);
        Assert.Equal("xx", chip.DisplayName);
    }

    [Fact]
    public void EnglishOutputVariant_LoadsFromSettings()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default with { EnglishOutputVariant = EnglishOutputVariant.UnitedKingdom },
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        Assert.Equal(EnglishOutputVariant.UnitedKingdom, context.Sut.EnglishOutputVariant);
        Assert.Equal(EnglishOutputVariant.UnitedKingdom, context.Sut.SelectedEnglishOutputVariantOption?.Value);
    }

    [Fact]
    public void EnglishOutputVariant_Change_PersistsToSettings()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default,
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.SelectedEnglishOutputVariantOption = context.Sut.EnglishOutputVariantOptions
            .Single(option => option.Value == EnglishOutputVariant.UnitedStates);

        Assert.Equal(EnglishOutputVariant.UnitedStates, context.Settings.Object.Current.EnglishOutputVariant);
        context.Settings.Verify(
            service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()),
            Times.Once);
    }

    [Theory]
    [InlineData("auto", null, true)]
    [InlineData("en-GB", null, true)]
    [InlineData("de", null, false)]
    [InlineData("de", "en", true)]
    public void IsEnglishOutputVariantVisible_FollowsLanguageAndTarget(string language, string? target, bool expected)
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default,
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.Language = language;
        context.Sut.TranslationTargetLanguage = target;

        Assert.Equal(expected, context.Sut.IsEnglishOutputVariantVisible);
    }

    [Fact]
    public void GermanOutputVariant_LoadsFromSettings()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default with { GermanOutputVariant = GermanOutputVariant.Switzerland },
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        Assert.Equal(GermanOutputVariant.Switzerland, context.Sut.GermanOutputVariant);
        Assert.Equal(GermanOutputVariant.Switzerland, context.Sut.SelectedGermanOutputVariantOption?.Value);
    }

    [Fact]
    public void GermanOutputVariant_Change_PersistsToSettings()
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default,
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.SelectedGermanOutputVariantOption = context.Sut.GermanOutputVariantOptions
            .Single(option => option.Value == GermanOutputVariant.Switzerland);

        Assert.Equal(GermanOutputVariant.Switzerland, context.Settings.Object.Current.GermanOutputVariant);
        context.Settings.Verify(
            service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()),
            Times.Once);
    }

    [Theory]
    [InlineData("de-CH", null, true)]
    [InlineData("auto", null, false)]
    [InlineData("en", "de", true)]
    [InlineData("en", null, false)]
    public void IsGermanOutputVariantVisible_FollowsLanguageAndTarget(string language, string? target, bool expected)
    {
        using var context = new ViewModelTestContext(
            AppSettings.Default,
            new FakeAudioDeviceEnumerator(new FakeDevice(0, "Default Mic", 1, isDefault: true)));

        context.Sut.Language = language;
        context.Sut.TranslationTargetLanguage = target;

        Assert.Equal(expected, context.Sut.IsGermanOutputVariantVisible);
    }

    [Fact]
    public void ShortUtterancePunctuationEnabled_LoadsFromSettings()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Default Mic", 1, isDefault: true)
        );
        using var context = new ViewModelTestContext(
            AppSettings.Default with { ShortUtterancePunctuationEnabled = false },
            devices
        );

        Assert.False(context.Sut.ShortUtterancePunctuationEnabled);
    }

    [Fact]
    public void ShortUtterancePunctuationEnabled_Change_PersistsToSettings()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Default Mic", 1, isDefault: true)
        );
        using var context = new ViewModelTestContext(AppSettings.Default, devices);

        context.Sut.ShortUtterancePunctuationEnabled = false;

        context.Settings.Verify(
            service => service.Update(It.Is<Func<AppSettings, AppSettings>>(
                mutation => !mutation(AppSettings.Default).ShortUtterancePunctuationEnabled)),
            Times.Once
        );
    }

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

            // A fake runner keeps the capability snapshot from probing host commands.
            var commands = new SystemCommandAvailabilityService(new FakeProcessRunner());
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
