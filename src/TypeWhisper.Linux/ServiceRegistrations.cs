using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux;

/// <summary>
/// DI wiring for the Linux host. Mirrors the registration shape of
/// TypeWhisper.Windows/App.xaml.cs but substitutes Linux-native service
/// implementations for the Win32/WPF ones. Services that aren't portable
/// to Linux (Velopack updater, SMTC MediaPause, Core Audio ducking,
/// SupporterDiscord, License server) are omitted from v1.
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
        services.AddSingleton<IDictionaryService>(
            new DictionaryService(Path.Combine(dataPath, "dictionary.json")));
        services.AddSingleton<IVocabularyBoostingService, VocabularyBoostingService>();
        services.AddSingleton<ISnippetService>(
            new SnippetService(Path.Combine(dataPath, "snippets.json")));
        services.AddSingleton<IProfileService>(
            new ProfileService(Path.Combine(dataPath, "profiles.json")));
        services.AddSingleton<IPromptActionService>(
            new PromptActionService(Path.Combine(dataPath, "prompt-actions.json")));
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();

        // Plugin subsystem
        services.AddSingleton<PluginEventBus>();
        services.AddSingleton<PluginLoader>();

        // Linux-native platform services
        services.AddSingleton<IActiveWindowService, ActiveWindowService>();
        services.AddSingleton<AudioRecordingService>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TextInsertionService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<DictationOrchestrator>();

        // Deferred until implementations land:
        //   IAudioDuckingService  (pactl per-sink-input volume)
        //   IMediaPauseService    (Tmds.DBus -> MPRIS2)
        //   ITranslationService   (needs PluginManager port)

        // ViewModels
        services.AddTransient<MainWindowViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }
}
