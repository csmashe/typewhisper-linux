using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Hotkey.Portal;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux;

/// <summary>
/// DI wiring for the Linux host. Keeps registrations Linux-native and omits
/// Windows-only services such as Win32 hotkeys, WPF UI, Velopack, SMTC media
/// pause, Core Audio ducking, supporter Discord, and license server flows.
/// </summary>
internal static class ServiceRegistrations
{
    public static void Register(HostBuilderContext context, IServiceCollection services)
    {
        var dataPath = TypeWhisperEnvironment.DataPath;

        // Core — settings & JSON-file-backed data services (all portable)
        services.AddSingleton<ISettingsService>(
            new SettingsService(TypeWhisperEnvironment.SettingsFilePath));
        services.AddSingleton<IErrorLogService>(new ErrorLogService(dataPath));
        services.AddSingleton<IHistoryService>(
            new HistoryService(Path.Combine(dataPath, "history.json"), TypeWhisperEnvironment.AudioPath));
        services.AddSingleton<RecentTranscriptionStore>();
        services.AddSingleton<IDictionaryService>(
            new DictionaryService(Path.Combine(dataPath, "dictionary.json")));
        services.AddSingleton<IVocabularyBoostingService, VocabularyBoostingService>();
        services.AddSingleton<ISnippetService>(
            new SnippetService(Path.Combine(dataPath, "snippets.json")));
        services.AddSingleton<IProfileService>(
            new ProfileService(Path.Combine(dataPath, "profiles.json")));
        services.AddSingleton<IPromptActionService>(
            new PromptActionService(Path.Combine(dataPath, "prompt-actions.json")));
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CorrectionSuggestionService>();
        services.AddSingleton<IHistoryInsightsService, HistoryInsightsService>();
        services.AddSingleton<IdeFileReferenceService>();
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();
        services.AddSingleton<ITranslationService, TranslationService>();

        // Plugin subsystem
        services.AddSingleton<PluginEventBus>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginManager>();
        services.AddSingleton<PluginRegistryService>();
        services.AddSingleton<ModelManagerService>();

        // Linux-native platform services
        services.AddSingleton<ActiveWindowService>();
        services.AddSingleton<IActiveWindowService>(sp => sp.GetRequiredService<ActiveWindowService>());
        services.AddSingleton<IAudioDuckingService, AudioDuckingService>();
        services.AddSingleton<IMediaPauseService, MediaPauseService>();
        services.AddSingleton<SystemCommandAvailabilityService>();
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<AudioFileService>();
        services.AddSingleton<IFileTranscriptionProcessor, FileTranscriptionProcessor>();
        services.AddSingleton<AudioPlaybackService>();
        services.AddSingleton<SessionAudioFileService>();
        services.AddSingleton<SoundFeedbackService>();
        services.AddSingleton<SpeechFeedbackService>();
        services.AddSingleton<SharpHookGlobalShortcutBackend>();
        services.AddSingleton<EvdevGlobalShortcutBackend>();
        services.AddSingleton<XdgPortalGlobalShortcutsBackend>();
        services.AddSingleton<BackendSelector>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TextInsertionService>();
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
        services.AddSingleton<SettingsBackupService>();
        services.AddSingleton<HttpApiService>();
        services.AddSingleton<CliInstallService>();
        services.AddSingleton<WatchFolderService>();

        // ViewModels — section VMs are singletons so state stays consistent
        // across the sidebar nav and the onboarding wizard.
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<DictationOverlayViewModel>();
        services.AddSingleton<GeneralSectionViewModel>();
        services.AddSingleton<AppearanceSectionViewModel>();
        services.AddSingleton<AdvancedSectionViewModel>();
        services.AddSingleton<ShortcutsSectionViewModel>();
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

        // Windows
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DictationOverlayWindow>();
        services.AddTransient<PromptPaletteWindow>();
        services.AddTransient<RecentTranscriptionsPaletteWindow>();
        services.AddTransient<WelcomeWizard>();
    }
}
