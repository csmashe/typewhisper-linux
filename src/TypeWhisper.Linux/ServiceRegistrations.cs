using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux;

/// <summary>
/// DI wiring for the Linux host. Mirrors the registration shape of
/// TypeWhisper.Windows but substitutes Linux-native service implementations
/// for the Win32/WPF ones. Phase-1 services are wired incrementally.
/// </summary>
internal static class ServiceRegistrations
{
    public static void Register(HostBuilderContext context, IServiceCollection services)
    {
        // Plugin subsystem
        services.AddSingleton<PluginEventBus>();
        services.AddSingleton<PluginLoader>();

        // Linux-native platform services — populate as each is implemented:
        //   AudioRecordingService  (PortAudioSharp2 / OpenAL)
        //   HotkeyService          (SharpHook; XDG portal GlobalShortcuts on Wayland)
        //   TextInsertionService   (xdotool on X11; ydotool fallback for Wayland)
        //   TrayIconService        (Avalonia.Controls.TrayIcon -> StatusNotifierItem)
        //   ActiveWindowService    (xdotool — implemented, see Services/ActiveWindowService.cs)
        //   ApiKeyProtection       (Tmds.LibSecret eventually; AES-derived-key fallback)
        //   MediaPauseService      (Tmds.DBus -> MPRIS2)
        //   AudioDuckingService    (pactl per-sink-input volume)
        //   StartupService         (XDG autostart — implemented, see Services/StartupService.cs)

        // Core service wiring (ProfileService, HistoryService, DictionaryService,
        // SnippetService, PostProcessingPipeline, VocabularyBoostingService,
        // PromptActionService, SettingsService, etc.) to be ported from
        // TypeWhisper.Windows/App.xaml.cs in a follow-up commit.

        // ViewModels
        services.AddTransient<MainWindowViewModel>();

        // Windows
        services.AddSingleton<MainWindow>();
    }
}
