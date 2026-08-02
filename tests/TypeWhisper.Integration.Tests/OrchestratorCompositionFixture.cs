// The accessor block below is this fixture's API for the tests that compose against it, kept
// uniform across every service it owns; the ones with no reader yet are not fixture internals.
// ReSharper disable MemberCanBePrivate.Global
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Insertion;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Integration.Tests;

internal sealed class OrchestratorCompositionFixture : IAsyncDisposable
{
    private readonly List<IDisposable> _eventSubscriptions = [];
    private readonly ConcurrentDictionary<int, DictationSessionResult> _results = new();
    private readonly ConcurrentDictionary<
        int,
        TaskCompletionSource<DictationSessionResult>
    > _resultWaiters = new();
    private int _disposed;

    internal OrchestratorCompositionFixture(bool soundFeedbackEnabled = false)
    {
        IntegrationEnvironment.ResetApplicationState();
        TypeWhisperEnvironment.EnsureDirectories();

        var services = new ServiceCollection();
        ServiceRegistrations.Register(services);

        Settings = (SettingsService)(services
            .Single(descriptor => descriptor.ServiceType == typeof(ISettingsService))
            .ImplementationInstance
            ?? throw new InvalidOperationException("The production settings registration changed."));
        Settings.Save(
            AppSettings.Default with
            {
                SelectedModelId = ModelManagerService.GetPluginModelId(
                    RecordingTranscriptionPlugin.Id,
                    RecordingTranscriptionPlugin.ModelId
                ),
                SelectedMicrophoneDevice = 0,
                LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu,
                AutoPaste = true,
                SaveToHistoryEnabled = true,
                SoundFeedbackEnabled = soundFeedbackEnabled,
                SpokenFeedbackEnabled = false,
                LiveTranscriptionEnabled = false,
                TranscribeShortQuietClipsAggressively = true,
                AudioDuckingEnabled = true,
                PauseMediaDuringRecording = true,
                TargetAppCorrectionLearningEnabled = false,
                MemoryEnabled = false,
                CommandModeEnabled = false,
                CleanupLevel = CleanupLevel.None,
            }
        );

        ProcessRunner = new HeadlessProcessRunner();
        CueProcessRunner = new GatedCueProcessRunner();
        SystemAudio = new RecordingSystemAudio();
        AudioBoundary = new RecordingAudioBoundary();
        PlaybackBoundary = new RecordingPlaybackBoundary();
        DeviceWatcher = new HeadlessDefaultDeviceWatcher();
        SessionActivity = new HeadlessSessionActivityMonitor();
        AtSpi = new HeadlessAtSpiClient();
        InsertionPlatform = new RecordingTextInsertionPlatform();

        var soundsDirectory = Path.Join(TypeWhisperEnvironment.BasePath, "IntegrationSounds");
        Directory.CreateDirectory(soundsDirectory);
        File.WriteAllBytes(Path.Join(soundsDirectory, "start.wav"), [0]);

        ReplaceExternalBoundaries(
            services,
            ProcessRunner,
            SystemAudio,
            AudioBoundary,
            PlaybackBoundary,
            DeviceWatcher,
            SessionActivity,
            AtSpi,
            InsertionPlatform
        );
        services.Replace(
            ServiceDescriptor.Singleton(
                new SoundFeedbackService(CueProcessRunner, "integration-player", soundsDirectory)
            )
        );
        services.Replace(
            ServiceDescriptor.Singleton<SpeechFeedbackService>(sp =>
                new SpeechFeedbackService(
                    sp.GetRequiredService<ISettingsService>(),
                    sp.GetRequiredService<PluginManager>(),
                    new UnconfiguredTtsProvider()
                )
            )
        );

        Provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );

        Plugin = new RecordingTranscriptionPlugin();
        PluginManager = Provider.GetRequiredService<PluginManager>();
        InjectTranscriptionEngine(PluginManager, Plugin);

        EventBus = Provider.GetRequiredService<PluginEventBus>();
        RecordingStarted = new TaskCompletionSource<RecordingStartedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TranscriptionPublished = new TaskCompletionSource<TranscriptionCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _eventSubscriptions.Add(
            EventBus.Subscribe<RecordingStartedEvent>(started =>
            {
                RecordingStarted.TrySetResult(started);
                return Task.CompletedTask;
            })
        );
        _eventSubscriptions.Add(
            EventBus.Subscribe<TranscriptionCompletedEvent>(completed =>
            {
                TranscriptionPublished.TrySetResult(completed);
                return Task.CompletedTask;
            })
        );

        Orchestrator = Provider.GetRequiredService<DictationOrchestrator>();
        SessionResults = Provider.GetRequiredService<DictationSessionResultStore>();
        Orchestrator.SessionCompleted += SessionResults.Record;
        Orchestrator.SessionCompleted += OnSessionCompleted;
        Orchestrator.TranscriptionCompleted += OnTranscriptionCompleted;
        Orchestrator.Initialize();
        ProcessRunner.Reset();

        Audio = Provider.GetRequiredService<AudioRecordingService>();
        History = Provider.GetRequiredService<IHistoryService>();
        RecentStore = Provider.GetRequiredService<RecentTranscriptionStore>();
    }

    internal ServiceProvider Provider { get; }
    internal SettingsService Settings { get; }
    internal DictationOrchestrator Orchestrator { get; }
    internal AudioRecordingService Audio { get; }
    internal IHistoryService History { get; }
    internal RecentTranscriptionStore RecentStore { get; }
    internal DictationSessionResultStore SessionResults { get; }
    internal PluginManager PluginManager { get; }
    internal PluginEventBus EventBus { get; }
    internal RecordingTranscriptionPlugin Plugin { get; }
    internal RecordingTextInsertionPlatform InsertionPlatform { get; }
    internal RecordingSystemAudio SystemAudio { get; }
    internal RecordingAudioBoundary AudioBoundary { get; }
    internal RecordingPlaybackBoundary PlaybackBoundary { get; }
    internal HeadlessDefaultDeviceWatcher DeviceWatcher { get; }
    internal HeadlessSessionActivityMonitor SessionActivity { get; }
    internal HeadlessAtSpiClient AtSpi { get; }
    internal HeadlessProcessRunner ProcessRunner { get; }
    internal GatedCueProcessRunner CueProcessRunner { get; }
    internal TaskCompletionSource<RecordingStartedEvent> RecordingStarted { get; }
    internal TaskCompletionSource<TranscriptionCompletedEvent> TranscriptionPublished { get; }
    internal TaskCompletionSource<string> TranscriptionReady { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    internal static void ReplaceExternalBoundaries(
        IServiceCollection services,
        HeadlessProcessRunner processRunner,
        RecordingSystemAudio systemAudio,
        RecordingAudioBoundary audioBoundary,
        RecordingPlaybackBoundary playbackBoundary,
        HeadlessDefaultDeviceWatcher deviceWatcher,
        HeadlessSessionActivityMonitor sessionActivity,
        HeadlessAtSpiClient atSpi,
        RecordingTextInsertionPlatform insertionPlatform
    )
    {
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(processRunner));
        services.Replace(ServiceDescriptor.Singleton<IAudioDuckingService>(systemAudio));
        services.Replace(ServiceDescriptor.Singleton<IMediaPauseService>(systemAudio));
        services.Replace(
            ServiceDescriptor.Singleton<IDefaultDeviceChangeWatcher>(deviceWatcher)
        );
        services.Replace(
            ServiceDescriptor.Singleton<ISessionActivityMonitor>(sessionActivity)
        );
        services.Replace(ServiceDescriptor.Singleton<IAtSpiEventClient>(atSpi));
        services.Replace(
            ServiceDescriptor.Singleton(new BackendSelector(
                static () => new HeadlessShortcutBackend()
            ))
        );
        services.Replace(
            ServiceDescriptor.Singleton<AudioRecordingService>(sp =>
                audioBoundary.CreateService(sp.GetRequiredService<IErrorLogService>())
            )
        );
        services.Replace(
            ServiceDescriptor.Singleton(playbackBoundary.CreateService())
        );
        services.Replace(
            ServiceDescriptor.Singleton<TextInsertionService>(sp =>
                new TextInsertionService(
                    insertionPlatform,
                    sp.GetRequiredService<IErrorLogService>(),
                    sp.GetRequiredService<IPasteConfirmationSource>()
                )
            )
        );
    }

    internal void FeedNonSilentAudio()
    {
        Audio.ProcessAudioBufferForTest(Enumerable.Repeat(0.2f, 16_000).ToArray());
    }

    internal Task<DictationSessionResult> WaitForResultAsync(int sessionId)
    {
        if (_results.TryGetValue(sessionId, out var result))
        {
            return Task.FromResult(result);
        }

        var waiter = _resultWaiters.GetOrAdd(
            sessionId,
            static _ => new TaskCompletionSource<DictationSessionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )
        );
        if (_results.TryGetValue(sessionId, out result))
        {
            waiter.TrySetResult(result);
        }

        return BoundedTest.WaitAsync(waiter.Task);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CueProcessRunner.ReleaseCue();
        Orchestrator.TranscriptionCompleted -= OnTranscriptionCompleted;
        Orchestrator.SessionCompleted -= OnSessionCompleted;
        Orchestrator.SessionCompleted -= SessionResults.Record;
        foreach (var subscription in _eventSubscriptions)
        {
            subscription.Dispose();
        }

        await BoundedTest.WaitAsync(Task.Run(Orchestrator.Dispose));
        await BoundedTest.WaitAsync(Task.Run(async () => await Provider.DisposeAsync()));
    }

    private void OnSessionCompleted(DictationSessionResult result)
    {
        _results[result.SessionId] = result;
        if (_resultWaiters.TryRemove(result.SessionId, out var waiter))
        {
            waiter.TrySetResult(result);
        }
    }

    private void OnTranscriptionCompleted(object? sender, string text)
    {
        TranscriptionReady.TrySetResult(text);
    }

    private static void InjectTranscriptionEngine(
        PluginManager pluginManager,
        ITranscriptionEngineRole plugin
    )
    {
        var field = typeof(PluginManager).GetField(
            "_transcriptionEngines",
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new MissingFieldException(
            typeof(PluginManager).FullName,
            "_transcriptionEngines"
        );
        field.SetValue(pluginManager, new List<ITranscriptionEngineRole> { plugin });
    }
}
