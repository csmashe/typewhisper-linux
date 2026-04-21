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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Host.Services;

            // Eagerly bootstrap the app state that the UI depends on.
            BootstrapAsync(services).GetAwaiter().GetResult();

            desktop.MainWindow = services.GetRequiredService<MainWindow>();

            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            tray.ShowSettingsRequested += (_, _) => desktop.MainWindow?.Show();
            tray.ExitRequested += (_, _) => desktop.Shutdown();

            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task BootstrapAsync(IServiceProvider services)
    {
        // Load settings before anything that depends on them.
        var settings = services.GetRequiredService<ISettingsService>();
        settings.Load();

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
