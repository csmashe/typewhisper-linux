using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Insertion;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Setup;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux;

/// <summary>
///     DI wiring for the Linux host. Omits Windows-only services (Win32 hotkeys,
///     WPF UI, Velopack, SMTC, Core Audio, Discord, license server).
/// </summary>
internal static class ServiceRegistrations
{
    public static void Register(IServiceCollection services)
    {
        var dataPath = TypeWhisperEnvironment.DataPath;

        // Core — settings & JSON-file-backed data services (all portable)
        services.AddSingleton<ISettingsService>(
            new SettingsService(TypeWhisperEnvironment.SettingsFilePath)
        );
        var errorLog = new ErrorLogService(dataPath);
        // EnsureDirectories can only reach the boot log, which a desktop-entry launch never shows.
        // Repeat it here so the About screen and exported diagnostics carry it too.
        if (!TypeWhisperEnvironment.AudioDirectoryIsOwnerOnly)
        {
            var warning =
                $"Recordings folder '{TypeWhisperEnvironment.AudioPath}' could not be made "
                + "owner-only; recordings saved there may be readable by other users of this "
                + "machine.";

            // A standing property of the mount, not an event, and the log is a bounded ring
            // persisted across launches — appending every startup would evict real failures.
            if (errorLog.Entries.All(e => e.Message != warning))
            {
                errorLog.AddEntry(warning, ErrorCategory.Recording);
            }
        }

        services.AddSingleton<IErrorLogService>(errorLog);
        services.AddSingleton(sp =>
            new UiOperationGuard(
                sp.GetRequiredService<IErrorLogService>(),
                async message =>
                {
                    var dialog = new MessageDialogWindow();
                    await dialog.ShowMessageAsync(
                        Loc.Instance["Common.OperationFailedTitle"],
                        message
                    );
                },
                (operation, reason) =>
                    Loc.Instance.GetString("Common.OperationFailed", operation, reason)
            )
        );
        services.AddSingleton<IHistoryService>(
            new HistoryService(
                Path.Join(dataPath, "history.json"),
                TypeWhisperEnvironment.AudioPath
            )
        );
        services.AddSingleton<RecentTranscriptionStore>();
        services.AddSingleton<IDictionaryService>(
            new DictionaryService(Path.Join(dataPath, "dictionary.json"))
        );
        services.AddSingleton<IVocabularyBoostingService, VocabularyBoostingService>();
        services.AddSingleton<ISnippetService>(
            new SnippetService(Path.Join(dataPath, "snippets.json"))
        );
        services.AddSingleton<IProfileService>(sp =>
            new ProfileService(
                Path.Join(dataPath, "profiles.json"),
                sp.GetRequiredService<IErrorLogService>()
            )
        );
        services.AddSingleton<IPromptActionService>(sp =>
            new PromptActionService(
                Path.Join(dataPath, "prompt-actions.json"),
                sp.GetRequiredService<IErrorLogService>()
            )
        );
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CorrectionSuggestionService>();
        services.AddSingleton<IHistoryInsightsService, HistoryInsightsService>();
        services.AddSingleton<IdeFileReferenceService>();
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();
        services.AddSingleton<ITranslationService, TranslationService>();

        // Plugin subsystem
        services.AddSingleton<PluginEventBus>();
        services.AddSingleton(new PluginLoader(TypeWhisperEnvironment.PluginDataPath));
        services.AddSingleton<PluginManager>();
        services.AddSingleton<PluginRegistryService>();
        services.AddSingleton<ModelManagerService>();

        // Linux-native platform services
        services.AddSingleton<IDetectionFailureTracker, DetectionFailureTracker>();
        services.AddSingleton<IActiveWindowProvider, HyprlandActiveWindowProvider>();
        services.AddSingleton<IActiveWindowProvider, SwayActiveWindowProvider>();
        services.AddSingleton<IActiveWindowProvider, KWinActiveWindowProvider>();
        // Window Calls extension wins on GNOME when installed — modern
        // GNOME blocks the built-in Introspect API for unprivileged apps.
        services.AddSingleton<IActiveWindowProvider, GnomeWindowCallsProvider>();
        services.AddSingleton<IActiveWindowProvider, GnomeShellActiveWindowProvider>();
        services.AddSingleton<IActiveWindowProvider, XdotoolActiveWindowProvider>();
        services.AddSingleton<AtSpiUrlExtractor>();
        // Event-driven AT-SPI client + silent target-app correction learning
        // (Wispr-Flow-style). The client holds one a11y-bus connection open; the
        // learning service arms a tracking window after each qualifying insertion.
        services.AddSingleton<AtSpiEventClient>();
        services.AddSingleton<IAtSpiEventClient>(sp => sp.GetRequiredService<AtSpiEventClient>());
        // Event-driven paste confirmation for TextInsertionService's clipboard restore.
        // Read-only over the AT-SPI client: it never starts the listeners itself, so the
        // insertion path is unchanged unless correction learning already turned them on.
        services.AddSingleton<IPasteConfirmationSource, AtSpiPasteConfirmation>();
        services.AddSingleton<TargetAppCorrectionLearningService>();
        // Toggles the session-bus accessibility flag (org.a11y.Status.IsEnabled) that
        // Chromium/Electron/Qt apps gate their accessibility tree on; most desktops leave it
        // off by default. Surfaced as a button in the Dictation settings when target-app
        // correction learning is enabled and the flag reads as off.
        services.AddSingleton<IAccessibilityBusActivation, AccessibilityBusActivationService>();
        services.AddSingleton<ActiveWindowService>();
        services.AddSingleton<IActiveWindowService>(sp =>
            sp.GetRequiredService<ActiveWindowService>()
        );
        services.AddSingleton<IAudioDuckingService, AudioDuckingService>();
        services.AddSingleton<IMediaPauseService, MediaPauseService>();
        services.AddSingleton<SystemCommandAvailabilityService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<UrlLauncher>();
        services.AddSingleton<ActionPluginExecutionHost>();
        // Reactive OS-default capture-device watcher (pactl subscribe); AudioRecordingService
        // starts/stops it as follow-default mode toggles and disposes it on teardown.
        services.AddSingleton<IDefaultDeviceChangeWatcher, PactlDefaultDeviceWatcher>();
        services.AddSingleton<AudioRecordingService>(sp =>
            new AudioRecordingService(
                sp.GetRequiredService<IErrorLogService>(),
                deviceWatcher: sp.GetRequiredService<IDefaultDeviceChangeWatcher>()
            )
        );
        services.AddSingleton<AudioFileService>();
        services.AddSingleton<IFileTranscriptionProcessor, FileTranscriptionProcessor>();
        services.AddSingleton<AudioPlaybackService>();
        services.AddSingleton(
            new SessionAudioFileService(TypeWhisperEnvironment.AudioPath)
        );
        services.AddSingleton<SoundFeedbackService>();
        services.AddSingleton<SpeechFeedbackService>();
        // The concrete backends are intentionally NOT registered: BackendSelector
        // mints fresh instances per Resolve() (they're disposed on backend switch,
        // so a shared singleton would be reused after disposal).
        // The logind monitor is process-scoped and each fresh evdev backend owns only
        // its event subscription; the DI container tears down the shared D-Bus matches.
        services.AddSingleton<ISessionActivityMonitor, LogindSessionActivityMonitor>();
        services.AddSingleton<BackendSelector>();
        services.AddSingleton<HotkeyService>();

        // Per-desktop shortcut writers; Settings panel calls IsCurrentDesktop() in order — first hit wins.
        services.AddSingleton<IDeShortcutWriter, GnomeShortcutWriter>();
        services.AddSingleton<IDeShortcutWriter, KdeShortcutWriter>();
        services.AddSingleton<IDeShortcutWriter, HyprlandShortcutWriter>();
        services.AddSingleton<IDeShortcutWriter, SwayShortcutWriter>();

        services.AddSingleton(sp => new TextInsertionService(
            sp.GetRequiredService<IErrorLogService>(),
            sp.GetRequiredService<SystemCommandAvailabilityService>(),
            sp.GetRequiredService<IPasteConfirmationSource>(),
            sp.GetRequiredService<IProcessRunner>()
        ));
        services.AddSingleton<YdotoolSetupHelper>();
        services.AddSingleton<InputAccessSetupHelper>();
        services.AddSingleton<BrowserAccessibilitySetupHelper>();
        services.AddSingleton<CudaLibraryPathSetupService>();
        services.AddSingleton<GnomeWindowCallsSetupHelper>();

        // Onboarding checklist tasks. Each self-gates via AppliesToThisMachine();
        // adding a new desktop/session means registering here — no wizard changes needed.
        services.AddSingleton<PackageInstaller>();
        services.AddSingleton<ISetupTask, ClipboardSetupTask>();
        services.AddSingleton<ISetupTask, AutoPasteSetupTask>();
        services.AddSingleton<ISetupTask, GlobalHotkeySetupTask>();
        services.AddSingleton<ISetupTask, ActiveWindowSetupTask>();
        services.AddSingleton<ISetupTask, KwinActiveWindowSetupTask>();
        services.AddSingleton<ISetupTask, FfmpegSetupTask>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<DictationOrchestrator>();
        services.AddSingleton<PromptProcessingService>();
        services.AddSingleton<LlmCleanupService>();
        services.AddSingleton<PromptPaletteService>();
        services.AddSingleton<TransformSelectionService>();
        services.AddSingleton<RecentTranscriptionsService>();
        services.AddSingleton<MemoryService>();
        services.AddSingleton<BundledPluginDeployer>();
        services.AddSingleton<HistoryRetentionCoordinator>();
        services.AddSingleton<LinuxPreferencesService>();
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<SecretProtectionMigrationService>();
        services.AddSingleton(sp =>
            new SettingsBackupService(
                TypeWhisperEnvironment.BasePath,
                secretMigration: sp.GetRequiredService<SecretProtectionMigrationService>()
            )
        );
        services.AddSingleton<ApiDiscoveryFile>();
        services.AddSingleton<DictationSessionResultStore>();
        services.AddSingleton<HttpApiService>();
        services.AddSingleton<CliInstallService>();
        services.AddSingleton<WatchFolderService>();
        services.AddSingleton<ControlSocketServer>();

        // Section VMs are singletons so state stays consistent across sidebar nav and wizard.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DictationOverlayViewModel>();
        services.AddSingleton<GeneralSectionViewModel>();
        services.AddSingleton<AppearanceSectionViewModel>();
        services.AddSingleton<AdvancedSectionViewModel>();
        services.AddSingleton<ShortcutsSectionViewModel>();
        services.AddSingleton<TextInsertionSectionViewModel>();
        services.AddSingleton<FileTranscriptionSectionViewModel>();
        services.AddSingleton<RecorderSectionViewModel>();
        services.AddSingleton<PluginsSectionViewModel>();
        services.AddSingleton<HistorySectionViewModel>();
        services.AddSingleton<DictionarySectionViewModel>();
        services.AddSingleton<SnippetsSectionViewModel>();
        services.AddSingleton<ProfilesSectionViewModel>();
        services.AddSingleton<PromptsSectionViewModel>();
        services.AddSingleton<DashboardSectionViewModel>();
        services.AddSingleton<DictationSectionViewModel>();
        services.AddSingleton<AboutSectionViewModel>();
        services.AddTransient<WelcomeWizardViewModel>();

        // Tiling WM recording indicator (desktop notification instead of overlay; no-op on DEs).
        services.AddSingleton<RecordingNotificationService>();
        // Tiling WM learned-corrections feedback: same suppressed-overlay situation as above,
        // so the "Learned X → Y" toast + Undo is delivered as a desktop notification instead.
        services.AddSingleton<LearnedCorrectionsNotificationService>();
        // Desktop-environment learned-corrections feedback: a dedicated toast window placed
        // beside the corrected element (inert on tiling WMs, which use the notification above).
        services.AddSingleton<LearnedCorrectionsToastController>();

        // Avalonia windows
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DictationOverlayWindow>();
        services.AddSingleton<LearnedCorrectionToastWindow>();
        services.AddTransient<PromptPaletteWindow>();
        services.AddTransient<RecentTranscriptionsPaletteWindow>();
        services.AddTransient<WelcomeWizard>();
    }
}
