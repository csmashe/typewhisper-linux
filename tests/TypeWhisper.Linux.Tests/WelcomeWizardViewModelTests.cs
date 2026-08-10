using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Setup;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

// `token` is the view model's lifetime token; Cleanup cancels it mid-test, so the
// .WaitAsync(timeout) guards must stay time-bounded and never forward it.
// ReSharper disable MethodSupportsCancellation
public sealed class WelcomeWizardViewModelTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task CleanupDuringModelDownload_CancelsTokenAndSuppressesLateCompletionMutations()
    {
        var releaseDownload = NewCompletionSource();
        var plugin = new FakeTranscriptionPlugin
        {
            DownloadImplementation = _ => releaseDownload.Task,
        };
        using var harness = CreateHarness(plugin);

        var nextTask = harness.ViewModel.NextCommand.ExecuteAsync(null);
        var token = await plugin.DownloadStarted.Task.WaitAsync(s_testTimeout);

        harness.ViewModel.Cleanup();
        var statusAfterCleanup = harness.ViewModel.ModelStatus;
        var isDownloadingAfterCleanup = harness.ViewModel.IsModelDownloading;

        Assert.True(token.IsCancellationRequested);

        releaseDownload.SetResult();
        await nextTask.WaitAsync(s_testTimeout);

        harness.Settings.Verify(
            settings => settings.Save(It.IsAny<AppSettings>()),
            Times.Never
        );
        Assert.Equal(0, harness.ViewModel.StepIndex);
        Assert.Equal(statusAfterCleanup, harness.ViewModel.ModelStatus);
        Assert.Equal(isDownloadingAfterCleanup, harness.ViewModel.IsModelDownloading);
        Assert.False(Assert.Single(harness.ViewModel.AvailableModels).IsDownloaded);
    }

    [Fact]
    public async Task CleanupDuringSetupAction_CancelsTokenAndSuppressesPostAwaitRowMutation()
    {
        var actionStarted = NewCompletionSource<CancellationToken>();
        var releaseAction = NewCompletionSource<SetupActionOutcome>();
        var setupTask = CreateSetupTask();
        setupTask
            .Setup(task => task.RunActionAsync(It.IsAny<CancellationToken>()))
            .Returns(
                (CancellationToken token) =>
                {
                    actionStarted.TrySetResult(token);
                    return releaseAction.Task;
                }
            );
        using var harness = CreateHarness(setupTasks: [setupTask.Object]);
        var row = new SetupTaskRow(setupTask.Object);
        harness.ViewModel.SetupItems.Add(row);

        var actionTask = harness.ViewModel.RunSetupActionCommand.ExecuteAsync(row);
        var token = await actionStarted.Task.WaitAsync(s_testTimeout);
        var actionMessageWhileRunning = row.ActionMessage;

        harness.ViewModel.Cleanup();

        Assert.True(token.IsCancellationRequested);

        releaseAction.SetResult(new SetupActionOutcome(true, "late success"));
        await actionTask.WaitAsync(s_testTimeout);

        Assert.True(row.IsBusy);
        Assert.Equal(SetupTaskStatusKind.Working, row.Kind);
        Assert.Equal(actionMessageWhileRunning, row.ActionMessage);
        setupTask.Verify(
            task => task.EvaluateAsync(It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task LifetimeCancellation_DoesNotSurfaceModelFailureStatus()
    {
        var plugin = new FakeTranscriptionPlugin
        {
            DownloadImplementation = token =>
                token.CanBeCanceled
                    ? Task.Delay(Timeout.InfiniteTimeSpan, token)
                    : Task.FromCanceled(new CancellationToken(canceled: true)),
        };
        using var harness = CreateHarness(plugin);

        var nextTask = harness.ViewModel.NextCommand.ExecuteAsync(null);
        var token = await plugin.DownloadStarted.Task.WaitAsync(s_testTimeout);
        var runningStatus = harness.ViewModel.ModelStatus;

        harness.ViewModel.Cleanup();
        await nextTask.WaitAsync(s_testTimeout);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal(runningStatus, harness.ViewModel.ModelStatus);
        Assert.NotEqual(
            Loc.Instance.GetString("Wizard.ModelFailed", "A task was canceled."),
            harness.ViewModel.ModelStatus
        );
        harness.Settings.Verify(
            settings => settings.Save(It.IsAny<AppSettings>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ModelDownloadWithoutCleanup_CompletesAndAppliesMutations()
    {
        var releaseDownload = NewCompletionSource();
        var plugin = new FakeTranscriptionPlugin
        {
            DownloadImplementation = _ => releaseDownload.Task,
        };
        using var harness = CreateHarness(plugin);

        var nextTask = harness.ViewModel.NextCommand.ExecuteAsync(null);
        var token = await plugin.DownloadStarted.Task.WaitAsync(s_testTimeout);

        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);

        releaseDownload.SetResult();
        await nextTask.WaitAsync(s_testTimeout);

        Assert.False(token.IsCancellationRequested);
        Assert.Equal(1, harness.ViewModel.StepIndex);
        Assert.False(harness.ViewModel.IsModelDownloading);
        Assert.True(Assert.Single(harness.ViewModel.AvailableModels).IsDownloaded);
        Assert.Equal(plugin.FullModelId, harness.Settings.Object.Current.SelectedModelId);
        Assert.Equal(
            Loc.Instance.GetString(
                "Wizard.ModelReady",
                Assert.IsType<WizardModelRow>(harness.ViewModel.SelectedModel).DisplayName
            ),
            harness.ViewModel.ModelStatus
        );
        harness.Settings.Verify(
            settings =>
                settings.Save(
                    It.Is<AppSettings>(saved => saved.SelectedModelId == plugin.FullModelId)
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task NextCommand_WhenModelDownloadThrows_ShowsModelFailureAndDoesNotAdvance()
    {
        const string expectedError = "download failed";
        var plugin = new FakeTranscriptionPlugin
        {
            DownloadImplementation = _ =>
                Task.FromException(new InvalidOperationException(expectedError)),
        };
        using var harness = CreateHarness(plugin);

        await harness.ViewModel.NextCommand.ExecuteAsync(null);

        Assert.Equal(0, harness.ViewModel.StepIndex);
        Assert.False(harness.ViewModel.IsModelDownloading);
        Assert.Equal(
            Loc.Instance.GetString("Wizard.ModelFailed", expectedError),
            harness.ViewModel.ModelStatus
        );
        Assert.False(Assert.Single(harness.ViewModel.AvailableModels).IsDownloaded);
        harness.Settings.Verify(
            settings => settings.Save(It.IsAny<AppSettings>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ToggleFirstDictationCommand_WhenAudioAlreadyOwned_ShowsStartFailureWithoutAdoptingOwner()
    {
        using var harness = CreateHarness();
        var foreignSession = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            harness.Audio.TryStartRecording(whisperModeEnabled: false)
        );

        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);

        Assert.Equal(
            Loc.Instance["Wizard.FirstDictationStartFailedGeneric"],
            harness.ViewModel.FirstDictationStatus
        );
        Assert.False(harness.ViewModel.IsFirstDictationRecording);
        Assert.True(harness.Audio.IsRecordingOwnedBy(foreignSession));

        harness.Audio.ProcessAudioBufferForTest([0.1f, -0.1f]);
        // ReSharper disable once MethodHasAsyncOverload -- synchronous teardown keeps this focused on ownership; StopRecordingAsync would add its 120ms drain for nothing.
        Assert.True(harness.Audio.StopRecording(foreignSession).Length > 44);
    }

    [Fact]
    public async Task ToggleFirstDictationCommand_WhenOwnedStopThrows_ShowsRecordingFailedAndLeavesRecordingFalse()
    {
        const string expectedError = "stop failed";
        var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => throw new InvalidOperationException(expectedError)
        );
        using var harness = CreateHarness(audio: audio);
        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);
        harness.Audio.ProcessAudioBufferForTest([0.1f, -0.1f]);

        var exception = await Record.ExceptionAsync(
            () => harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null)
        );

        Assert.Null(exception);
        Assert.Equal(
            Loc.Instance.GetString("Wizard.RecordingFailed", expectedError),
            harness.ViewModel.FirstDictationStatus
        );
        Assert.False(harness.ViewModel.IsFirstDictationRecording);
    }

    [Fact]
    public async Task ToggleFirstDictationCommand_WhenCaptureSessionIsStale_ShowsNoAudioCaptured()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);
        harness.Audio.ProcessAudioBufferForTest([0.1f, -0.1f]);
        harness.Audio.Dispose();

        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);

        Assert.Equal(
            Loc.Instance["Wizard.NoAudioCaptured"],
            harness.ViewModel.FirstDictationStatus
        );
        Assert.False(harness.ViewModel.IsFirstDictationRecording);
    }

    [Fact]
    public async Task ToggleFirstDictationCommand_WhenNoModelCanBeAcquired_ShowsModelLoadFailed()
    {
        using var harness = CreateHarness();
        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);
        harness.Audio.ProcessAudioBufferForTest([0.1f, -0.1f]);

        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);

        Assert.Equal(
            Loc.Instance["Wizard.ModelLoadFailed"],
            harness.ViewModel.FirstDictationStatus
        );
        Assert.False(harness.ViewModel.IsFirstDictationRecording);
        Assert.Equal("", harness.ViewModel.FirstDictationText);
    }

    [Fact]
    public async Task ToggleFirstDictationCommand_WhenPluginTranscriptionThrows_ShowsTranscriptionFailed()
    {
        const string expectedError = "transcription failed";
        byte[]? transcribedAudio = null;
        var transcriptionInvocations = 0;
        var plugin = new FakeTranscriptionPlugin
        {
            TranscribeImplementation = (wav, _) =>
            {
                transcribedAudio = wav;
                transcriptionInvocations++;
                return Task.FromException<PluginTranscriptionResult>(
                    new InvalidOperationException(expectedError)
                );
            },
        };
        using var harness = CreateHarness(plugin);
        await harness.ViewModel.NextCommand.ExecuteAsync(null);
        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);
        harness.Audio.ProcessAudioBufferForTest([0.1f, -0.1f]);

        await harness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);

        Assert.Equal(1, transcriptionInvocations);
        Assert.True(Assert.IsType<byte[]>(transcribedAudio).Length > 44);
        Assert.Equal(
            Loc.Instance.GetString("Wizard.TranscriptionFailed", expectedError),
            harness.ViewModel.FirstDictationStatus
        );
        Assert.False(harness.ViewModel.IsFirstDictationRecording);
        Assert.Equal("", harness.ViewModel.FirstDictationText);
    }

    [Fact]
    public void ExtensionRow_UsesNormalizedDescriptorMetadata()
    {
        var plugin = new FakeTranscriptionPlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            Path.GetTempPath(),
            plugin.PluginId,
            plugin,
            PluginNetworkAccess.UserControlled,
            [PluginCategory.Transcription, PluginCategory.Tts]
        );
        using var harness = CreateHarness(
            plugin,
            loadedPlugins: [loaded]
        );

        var row = Assert.Single(harness.ViewModel.ExtensionPlugins);

        Assert.Equal(PluginNetworkAccess.UserControlled, row.NetworkAccess);
        Assert.True(
            row.Categories.SetEquals(
                [PluginCategory.Transcription, PluginCategory.Tts]
            )
        );
        Assert.Equal("User controlled", row.LocationBadge);
        Assert.False(row.RanLocally);
    }

    [Fact]
    public async Task AudioLevel_DeliveryIsDirectAndActivityCleanupGatesLaterLevels()
    {
        var timeProvider = new ManualTimeProvider();
        var serviceJobs = new List<Action>();
        using var harness = CreateHarness(
            audioTimeProvider: timeProvider,
            audioPostToUiThread: serviceJobs.Add
        );

        harness.ViewModel.ToggleMicTestCommand.Execute(null);
        Assert.True(harness.ViewModel.IsMicTestRunning);
        harness.Audio.ProcessAudioBufferForTest([0.1f]);
        serviceJobs[0]();
        Assert.Equal(0.8, harness.ViewModel.MicLevel, precision: 5);

        harness.ViewModel.IsMicTestRunning = false;
        harness.ViewModel.MicLevel = 0;
        timeProvider.Advance(TimeSpan.FromMilliseconds(66));
        harness.Audio.ProcessAudioBufferForTest([0.2f]);
        serviceJobs[1]();
        Assert.Equal(0, harness.ViewModel.MicLevel);

        harness.ViewModel.Cleanup();
        timeProvider.Advance(TimeSpan.FromMilliseconds(66));
        harness.Audio.ProcessAudioBufferForTest([0.3f]);
        serviceJobs[2]();
        Assert.Equal(0, harness.ViewModel.MicLevel);

        var firstDictationJobs = new List<Action>();
        using var firstDictationHarness = CreateHarness(
            audioPostToUiThread: firstDictationJobs.Add
        );
        await firstDictationHarness.ViewModel.ToggleFirstDictationCommand.ExecuteAsync(null);
        Assert.True(firstDictationHarness.ViewModel.IsFirstDictationRecording);
        firstDictationHarness.Audio.ProcessAudioBufferForTest([0.2f]);
        firstDictationJobs[0]();
        Assert.Equal(1, firstDictationHarness.ViewModel.MicLevel);
    }

    private static Mock<ISetupTask> CreateSetupTask()
    {
        var setupTask = new Mock<ISetupTask>();
        setupTask.SetupGet(task => task.Id).Returns("test-setup");
        setupTask.SetupGet(task => task.Title).Returns("Test setup");
        setupTask.SetupGet(task => task.Severity).Returns(SetupTaskSeverity.Required);
        setupTask.Setup(task => task.AppliesToThisMachine()).Returns(true);
        setupTask
            .Setup(task => task.EvaluateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SetupTaskState(SetupTaskStatusKind.Satisfied, "ready"));
        return setupTask;
    }

    private static TestHarness CreateHarness(
        FakeTranscriptionPlugin? plugin = null,
        IReadOnlyList<ISetupTask>? setupTasks = null,
        IReadOnlyList<LoadedPlugin>? loadedPlugins = null,
        TimeProvider? audioTimeProvider = null,
        Action<Action>? audioPostToUiThread = null,
        AudioRecordingService? audio = null
    )
    {
        var settings = TestPluginManagerFactory.CreateSettings(AppSettings.Default);
        var pluginManager = TestPluginManagerFactory.Create(
            loadedPlugins: loadedPlugins
        );
        if (plugin is not null)
        {
            SetTranscriptionEngines(pluginManager, [plugin]);
        }

        var models = new ModelManagerService(pluginManager, settings.Object);
        var hotkey = new HotkeyService(
            new BackendSelector(static () => new TestShortcutBackend())
        );
        Debug.Assert(
            audio is null || (audioTimeProvider is null && audioPostToUiThread is null),
            "audio overrides the timeProvider/postToUiThread seams"
        );
        audio ??= new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            timeProvider: audioTimeProvider,
            postToUiThread: audioPostToUiThread
        );
        var commands = CreateCommandsWithoutHostProbes();
        var textInsertion = new TextInsertionService(new NoOpTextInsertionPlatform());
        var dictionary = new Mock<IDictionaryService>();
        var viewModel = new WelcomeWizardViewModel(
            models,
            pluginManager,
            hotkey,
            audio,
            commands,
            textInsertion,
            setupTasks ?? [],
            dictionary.Object,
            settings.Object,
            availableMics: []
        );
        return new TestHarness(
            viewModel,
            settings,
            models,
            pluginManager,
            hotkey,
            audio
        );
    }

    private static SystemCommandAvailabilityService CreateCommandsWithoutHostProbes()
    {
        var commands = (SystemCommandAvailabilityService)
            RuntimeHelpers.GetUninitializedObject(typeof(SystemCommandAvailabilityService));
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

    private static void SetTranscriptionEngines(
        PluginManager pluginManager,
        IReadOnlyList<ITranscriptionEngineRole> plugins
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
        field.SetValue(pluginManager, plugins.ToList());
    }

    private static TaskCompletionSource NewCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += (long)(elapsed.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class TestHarness : IDisposable
    {
        public TestHarness(
            WelcomeWizardViewModel viewModel,
            Mock<ISettingsService> settings,
            ModelManagerService models,
            PluginManager pluginManager,
            HotkeyService hotkey,
            AudioRecordingService audio
        )
        {
            ViewModel = viewModel;
            Settings = settings;
            Models = models;
            PluginManager = pluginManager;
            Hotkey = hotkey;
            Audio = audio;
        }

        public WelcomeWizardViewModel ViewModel { get; }
        public Mock<ISettingsService> Settings { get; }
        private ModelManagerService Models { get; }
        private PluginManager PluginManager { get; }
        private HotkeyService Hotkey { get; }
        public AudioRecordingService Audio { get; }

        public void Dispose()
        {
            ViewModel.Cleanup();
            Models.Dispose();
            PluginManager.Dispose();
            Hotkey.Dispose();
            Audio.Dispose();
        }
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        private const string ModelId = "test-model";
        private const string ModelDisplayName = "Test model";

        private bool _isDownloaded;

        public Func<CancellationToken, Task> DownloadImplementation { get; init; } =
            _ => Task.CompletedTask;

        public Func<byte[], CancellationToken, Task<PluginTranscriptionResult>>
            TranscribeImplementation { get; init; } =
            (_, _) =>
                Task.FromResult(
                    new PluginTranscriptionResult("", DetectedLanguage: null, 0)
                );

        public TaskCompletionSource<CancellationToken> DownloadStarted { get; } =
            NewCompletionSource<CancellationToken>();

        public string FullModelId => ModelManagerService.GetPluginModelId(PluginId, ModelId);
        public string PluginId => "com.test.welcome-wizard";
        public string PluginName => "Welcome wizard fake";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "welcome-wizard";
        public string ProviderDisplayName => "Test provider";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [
                new(ModelId, ModelDisplayName)
                {
                    IsRecommended = true,
                },
            ];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public bool SupportsModelDownload => true;

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectModel(string modelId)
        {
            SelectedModelId = modelId;
        }

        public bool IsModelDownloaded(string modelId)
        {
            return _isDownloaded;
        }

        public async Task DownloadModelAsync(
            string modelId,
            IProgress<double>? progress,
            CancellationToken ct
        )
        {
            DownloadStarted.TrySetResult(ct);
            await DownloadImplementation(ct);
            _isDownloaded = true;
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            return TranscribeImplementation(wavAudio, ct);
        }

        public void Dispose() { }
    }

    private sealed class NoOpTextInsertionPlatform : ITextInsertionPlatform
    {
        public bool IsClipboardSetAvailable => false;
        public bool IsPasteAvailable => false;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => false;
        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;

        public Task<string?> TryGetClipboardTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool> SetClipboardTextAsync(string text)
        {
            return Task.FromResult(false);
        }

        public Task<bool> ClipboardHasNonTextFormatsAsync()
        {
            return Task.FromResult(false);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            return Task.CompletedTask;
        }

        public string? GetActiveWindowId()
        {
            return null;
        }

        public Task<bool> ActivateWindowAsync(string windowId)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
        {
            return Task.FromResult(false);
        }

        public Task<bool> TypeTextAsync(string text)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SendCopyAsync(bool useTerminalShortcut)
        {
            return Task.FromResult(false);
        }

        public Task<bool> SendEnterAsync()
        {
            return Task.FromResult(false);
        }
    }
}
