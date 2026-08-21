using Moq;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationSectionViewModelLocalizationTests
{
    [Fact]
    public void LanguageChange_RebuildsLocalizedOptions_PreservesSelectionsWithoutSaving()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var settings = TestPluginManagerFactory.CreateSettings(
                new AppSettings
                {
                    Language = "fr",
                    CleanupLevel = CleanupLevel.High,
                    LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu,
                    AppInsertionStrategies = new Dictionary<string, TextInsertionStrategy>
                    {
                        ["firefox"] = TextInsertionStrategy.DirectTyping,
                    },
                }
            );
            using var pluginManager = TestPluginManagerFactory.Create();
            var commands = new SystemCommandAvailabilityService();
            using var models = new ModelManagerService(
                pluginManager,
                settings.Object,
                commands
            );
            using var audio = new AudioRecordingService(
                _ => { },
                () => 0,
                () => { }
            );
            // ReSharper disable once InconsistentNaming -- "a11y" is the standard numeronym for accessibility.
            var a11yBus = new Mock<IAccessibilityBusActivation>();
            a11yBus
                .Setup(bus => bus.IsActivatedAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            var dictation = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(
                typeof(DictationOrchestrator)
            );
            var sut = new DictationSectionViewModel(
                dictation,
                models,
                audio,
                settings.Object,
                pluginManager,
                commands,
                new CudaLibraryPathSetupService(),
                a11yBus.Object,
                () => []
            )
            {
                NewInsertionStrategy = TextInsertionStrategy.CopyOnly,
            };
            settings.Invocations.Clear();

            var accelerationBefore = sut.SelectedAccelerationOption!;
            var languageBefore = Assert.Single(
                sut.LanguageChoices,
                option => option.Code == "auto"
            );
            var cleanupBefore = sut.SelectedCleanupLevelOption!;
            var insertionBefore = sut.SelectedNewInsertionStrategyOption!;
            var appStrategyRow = Assert.Single(sut.AppInsertionStrategies);
            var appInsertionBefore = appStrategyRow.SelectedStrategyOption!;
            HashSet<string?> expectedPropertyChanges =
            [
                nameof(DictationSectionViewModel.AudioDuckingUnavailableReason),
                nameof(DictationSectionViewModel.MediaPauseUnavailableReason),
                nameof(DictationSectionViewModel.SoundFeedbackUnavailableReason),
                nameof(DictationSectionViewModel.CudaLibraryPathActionText),
                nameof(DictationSectionViewModel.DownloadCudaRuntimeText),
                nameof(DictationSectionViewModel.ClearGpuRuntimeText),
                nameof(DictationSectionViewModel.AccelerationStatusText),
            ];
            HashSet<string?> propertyChanges = [];

            sut.AccelerationOptions.CollectionChanged += (_, _) =>
                sut.SelectedAccelerationOption = null;
            sut.LanguageChoices.CollectionChanged += (_, _) =>
                sut.SelectedLanguageOption = null;
            sut.CleanupLevelOptions.CollectionChanged += (_, _) =>
                sut.SelectedCleanupLevelOption = null;
            sut.InsertionStrategyOptions.CollectionChanged += (_, _) =>
            {
                sut.SelectedNewInsertionStrategyOption = null;
                appStrategyRow.SelectedStrategyOption = null;
            };
            sut.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName);

            Loc.Instance.CurrentLanguage = "de";

            Assert.Superset(expectedPropertyChanges, propertyChanges);
            Assert.NotSame(accelerationBefore, sut.SelectedAccelerationOption);
            Assert.Equal(
                AppSettings.LocalModelAccelerationCpu,
                sut.SelectedAccelerationOption?.Value
            );
            var autoLanguageAfter = Assert.Single(
                sut.LanguageChoices,
                option => option.Code == "auto"
            );
            Assert.NotEqual(languageBefore.DisplayName, autoLanguageAfter.DisplayName);
            Assert.Equal("fr", sut.SelectedLanguageOption?.Code);
            Assert.NotEqual(cleanupBefore.DisplayName, sut.SelectedCleanupLevelOption?.DisplayName);
            Assert.NotSame(cleanupBefore, sut.SelectedCleanupLevelOption);
            Assert.Equal(CleanupLevel.High, sut.SelectedCleanupLevelOption?.Value);
            Assert.NotEqual(
                insertionBefore.DisplayName,
                sut.SelectedNewInsertionStrategyOption?.DisplayName
            );
            Assert.NotSame(insertionBefore, sut.SelectedNewInsertionStrategyOption);
            Assert.Equal(
                TextInsertionStrategy.CopyOnly,
                sut.SelectedNewInsertionStrategyOption?.Value
            );
            Assert.NotEqual(
                appInsertionBefore.DisplayName,
                appStrategyRow.SelectedStrategyOption?.DisplayName
            );
            Assert.Equal(TextInsertionStrategy.DirectTyping, appStrategyRow.Strategy);
            settings.Verify(
                service => service.Save(It.IsAny<AppSettings>()),
                Times.Never
            );
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
    }

    [Fact]
    public void PreviewLevel_DirectDeliveryRequiresAttachedNonRecordingPreview()
    {
        var settings = TestPluginManagerFactory.CreateSettings(AppSettings.Default);
        using var pluginManager = TestPluginManagerFactory.Create();
        var commands = new SystemCommandAvailabilityService();
        using var models = new ModelManagerService(
            pluginManager,
            settings.Object,
            commands
        );
        var serviceJobs = new List<Action>();
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            postToUiThread: serviceJobs.Add
        );
        // ReSharper disable once InconsistentNaming -- "a11y" is the standard numeronym for accessibility.
        var a11yBus = new Mock<IAccessibilityBusActivation>();
        a11yBus
            .Setup(bus => bus.IsActivatedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var dictation = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(
            typeof(DictationOrchestrator)
        );
        var sut = new DictationSectionViewModel(
            dictation,
            models,
            audio,
            settings.Object,
            pluginManager,
            commands,
            new CudaLibraryPathSetupService(),
            a11yBus.Object,
            () => []
        );

        sut.ActivatePreview();
        audio.ProcessAudioBufferForTest([0.1f]);
        serviceJobs[0]();
        Assert.Equal(0.8, sut.PreviewLevel, precision: 5);

        sut.DeactivatePreview();
        serviceJobs[1]();
        Assert.Equal(0, sut.PreviewLevel);

        Assert.True(audio.StartPreview());
        audio.ProcessAudioBufferForTest([0.2f]);
        serviceJobs[2]();
        Assert.Equal(0, sut.PreviewLevel);

        sut.ActivatePreview();
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            audio.TryStartRecording(whisperModeEnabled: false)
        );
        sut.IsRecording = true;
        audio.ProcessAudioBufferForTest([0.3f]);
        serviceJobs[3]();
        Assert.Equal(0, sut.PreviewLevel);

        // ReSharper disable once MethodHasAsyncOverload -- synchronous teardown keeps this focused on delivery guards.
        audio.StopRecording(session);
        sut.DeactivatePreview();
    }
}
