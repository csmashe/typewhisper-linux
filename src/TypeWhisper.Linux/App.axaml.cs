using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Host.Services;
            desktop.MainWindow = services.GetRequiredService<MainWindow>();

            // Tray icon — best effort; silently degrades on sessions without SNI
            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            tray.ShowSettingsRequested += (_, _) => desktop.MainWindow?.Show();
            tray.ExitRequested += (_, _) => desktop.Shutdown();

            // Dictation orchestrator — global hotkey → record → capture WAV
            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
