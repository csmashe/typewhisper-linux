using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Tray-menu Exit flips this; Close-button handler checks it to decide
    /// whether to actually quit or hide to the tray.
    /// </summary>
    public static bool ShuttingDown { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Host.Services;

            // Eagerly bootstrap the app state that the UI depends on.
            BootstrapAsync(services).GetAwaiter().GetResult();

            var main = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = main;

            // Close button → hide to tray instead of exiting, unless tray menu
            // fired Exit (which flips ShuttingDown first).
            main.Closing += (_, e) =>
            {
                if (!ShuttingDown)
                {
                    e.Cancel = true;
                    main.Hide();
                }
            };

            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            tray.ShowSettingsRequested += (_, _) => ShowMainWindow(main);
            tray.ExitRequested += (_, _) =>
            {
                ShuttingDown = true;
                TearDownAsync(services).GetAwaiter().GetResult();
                desktop.Shutdown();
            };

            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();

            // Launch minimized / hidden if --minimized was passed — tray icon
            // stays the entry point.
            if (Program.StartMinimized)
                main.Opened += (_, _) => main.Hide();

            // First-run onboarding wizard
            var settings = services.GetRequiredService<ISettingsService>();
            if (!settings.Current.HasCompletedOnboarding)
            {
                main.Opened += (_, _) =>
                {
                    (main.DataContext as ViewModels.MainWindowViewModel)?.OpenWizard();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowMainWindow(MainWindow window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
            window.WindowState = Avalonia.Controls.WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Best-effort ordered shutdown of services that own native threads.
    /// Runs before desktop.Shutdown() so the Host isn't left racing
    /// libuiohook / PortAudio on exit.
    /// </summary>
    private static async Task TearDownAsync(IServiceProvider services)
    {
        try
        {
            var hotkey = services.GetService<HotkeyService>();
            hotkey?.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Hotkey dispose failed: {ex.Message}"); }

        try
        {
            var tray = services.GetService<TrayIconService>();
            tray?.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Tray dispose failed: {ex.Message}"); }

        try
        {
            var models = services.GetService<ModelManagerService>();
            models?.UnloadModel();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Model unload failed: {ex.Message}"); }

        try
        {
            var audio = services.GetService<AudioRecordingService>();
            audio?.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Audio dispose failed: {ex.Message}"); }

        await Task.CompletedTask;
    }

    private static async Task BootstrapAsync(IServiceProvider services)
    {
        // Load settings before anything that depends on them.
        var settings = services.GetRequiredService<ISettingsService>();
        settings.Load();

        // First-run: install any bundled plugins that aren't already on disk.
        var deployer = services.GetRequiredService<BundledPluginDeployer>();
        deployer.DeployIfMissing();

        // Discover and activate plugins.
        var pluginManager = services.GetRequiredService<PluginManager>();
        await pluginManager.InitializeAsync();

        // Migrate old plain-name model IDs to plugin-prefixed form.
        var modelManager = services.GetRequiredService<ModelManagerService>();
        modelManager.MigrateSettings();

        // Auto-load the last-selected model if it's already downloaded.
        var selectedModel = settings.Current.SelectedModelId;
        if (!string.IsNullOrEmpty(selectedModel) && modelManager.IsDownloaded(selectedModel))
        {
            try
            {
                await modelManager.LoadModelAsync(selectedModel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Auto-load model failed: {ex.Message}");
            }
        }
    }
}
