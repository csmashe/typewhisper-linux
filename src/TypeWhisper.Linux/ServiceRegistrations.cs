using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TypeWhisper.Linux;

/// <summary>
/// DI wiring for the Linux host. Mirrors the registration shape of
/// TypeWhisper.Windows but substitutes Linux-native service implementations
/// for the Win32/WPF ones. Phase-1 services will be added here incrementally.
/// </summary>
internal static class ServiceRegistrations
{
    public static void Register(HostBuilderContext context, IServiceCollection services)
    {
        // Core singletons — add TypeWhisper.Core service wiring here (ProfileService,
        // HistoryService, DictionaryService, SnippetService, PostProcessingPipeline,
        // VocabularyBoostingService, PromptActionService, etc.) once the Windows
        // App.xaml.cs registrations are ported.

        // Linux-native platform services — populate as each is implemented:
        //   AudioRecordingService  (PortAudioSharp2 / OpenAL)
        //   HotkeyService          (SharpHook; XDG portal GlobalShortcuts on Wayland)
        //   TextInsertionService   (xdotool on X11; ydotool fallback for Wayland)
        //   TrayIconService        (Avalonia.Controls.TrayIcon -> StatusNotifierItem)
        //   ActiveWindowService    (_NET_ACTIVE_WINDOW via xdotool on X11)
        //   ApiKeyProtection       (Tmds.LibSecret -> GNOME Keyring / KWallet)
        //   MediaPauseService      (Tmds.DBus -> MPRIS2)
        //   AudioDuckingService    (pactl per-sink-input volume)
        //   StartupService         (~/.config/autostart/typewhisper.desktop)

        services.AddSingleton<MainWindow>();
    }
}
