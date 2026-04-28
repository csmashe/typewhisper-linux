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
            var settings = services.GetRequiredService<ISettingsService>();
            settings.Load();

            var main = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = main;

            // Close-button behavior is user-configurable. Default
            // (CloseToTray=false): X fully quits, same path as tray Exit.
            // With CloseToTray=true the window hides and the tray stays the
            // entry point. Tray Exit always quits (flips ShuttingDown first).
            var prefs = services.GetRequiredService<LinuxPreferencesService>();
            main.Closing += (_, e) =>
            {
                if (ShuttingDown) return;

                if (prefs.Current.CloseToTray)
                {
                    e.Cancel = true;
                    main.Hide();
                }
                else
                {
                    ShuttingDown = true;
                    TearDownAsync(services).GetAwaiter().GetResult();
                    // Let the close proceed; ClassicDesktopStyle shuts down
                    // when the main window closes.
                }
            };

            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            tray.ShowSettingsRequested += (_, _) =>
            {
                ShowMainWindow(main);
                (main.DataContext as ViewModels.MainWindowViewModel)?.Navigate<ViewModels.Sections.GeneralSectionViewModel>();
            };
            tray.ExitRequested += (_, _) =>
            {
                ShuttingDown = true;
                TearDownAsync(services).GetAwaiter().GetResult();
                desktop.Shutdown();
            };

            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();

            var overlay = services.GetRequiredService<Views.DictationOverlayWindow>();
            overlay.Initialize();

            // Sync the hotkey service's mode + binding with AppSettings. The
            // handler re-runs on every settings change so flipping the mode
            // in Settings → Shortcuts takes effect without a restart.
            //
            // On first apply we reconcile: if the persisted ToggleHotkey
            // doesn't parse or differs from the service's default, write the
            // service's current binding back to settings so subsequent
            // SettingsChanged events (e.g. user toggling SaveToHistory) don't
            // silently rebind the hotkey to an upstream default like
            // "Ctrl+Shift+F9" that the user never chose.
            var hotkey = services.GetRequiredService<HotkeyService>();
            ReconcileHotkeyOnStartup(hotkey, settings);
            var lastApplied = hotkey.CurrentHotkeyString;
            var lastPromptPaletteApplied = hotkey.CurrentPromptPaletteHotkeyString;
            var lastRecentTranscriptionsApplied = hotkey.CurrentRecentTranscriptionsHotkeyString;
            var lastCopyLastTranscriptionApplied = hotkey.CurrentCopyLastTranscriptionHotkeyString;
            var lastTransformSelectionApplied = hotkey.CurrentTransformSelectionHotkeyString;
            settings.SettingsChanged += s =>
            {
                hotkey.Mode = s.Mode;
                if (!string.IsNullOrWhiteSpace(s.ToggleHotkey)
                    && s.ToggleHotkey != lastApplied
                    && hotkey.TrySetHotkeyFromString(s.ToggleHotkey))
                {
                    lastApplied = hotkey.CurrentHotkeyString;
                }

                if (s.PromptPaletteHotkey != lastPromptPaletteApplied
                    && hotkey.TrySetPromptPaletteHotkeyFromString(s.PromptPaletteHotkey))
                {
                    lastPromptPaletteApplied = hotkey.CurrentPromptPaletteHotkeyString;
                }

                if (s.RecentTranscriptionsHotkey != lastRecentTranscriptionsApplied
                    && hotkey.TrySetRecentTranscriptionsHotkeyFromString(s.RecentTranscriptionsHotkey))
                {
                    lastRecentTranscriptionsApplied = hotkey.CurrentRecentTranscriptionsHotkeyString;
                }

                if (s.CopyLastTranscriptionHotkey != lastCopyLastTranscriptionApplied
                    && hotkey.TrySetCopyLastTranscriptionHotkeyFromString(s.CopyLastTranscriptionHotkey))
                {
                    lastCopyLastTranscriptionApplied = hotkey.CurrentCopyLastTranscriptionHotkeyString;
                }

                if (s.TransformSelectionHotkey != lastTransformSelectionApplied
                    && hotkey.TrySetTransformSelectionHotkeyFromString(s.TransformSelectionHotkey))
                {
                    lastTransformSelectionApplied = hotkey.CurrentTransformSelectionHotkeyString;
                }
            };

            var api = services.GetRequiredService<HttpApiService>();
            api.ApplySettings();
            settings.SettingsChanged += _ => api.ApplySettings();

            var promptPalette = services.GetRequiredService<PromptPaletteService>();
            hotkey.PromptPaletteRequested += (_, _) => _ = promptPalette.TogglePaletteAsync();

            var recentTranscriptions = services.GetRequiredService<RecentTranscriptionsService>();
            recentTranscriptions.FeedbackRequested += (message, isError) =>
            {
                // Feed this through the overlay feedback path by reusing
                // dictation status events rather than creating a second toast
                // implementation.
                Debug.WriteLine($"[RecentTranscriptions] {(isError ? "Error" : "Info")}: {message}");
            };
            hotkey.RecentTranscriptionsRequested += (_, _) => recentTranscriptions.TogglePalette();
            hotkey.CopyLastTranscriptionRequested += (_, _) => _ = recentTranscriptions.CopyLastTranscriptionToClipboardAsync();
            var transformSelection = services.GetRequiredService<TransformSelectionService>();
            hotkey.TransformSelectionRequested += (_, _) => _ = transformSelection.ToggleAsync();

            // Launch minimized / hidden if --minimized was passed.
            if (Program.StartMinimized)
                main.Opened += (_, _) => main.Hide();

            // First-run onboarding wizard.
            if (!settings.Current.HasCompletedOnboarding)
            {
                main.Opened += (_, _) =>
                {
                    (main.DataContext as ViewModels.MainWindowViewModel)?.OpenWizard();
                };
            }

            _ = BootstrapDeferredAsync(services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Upstream AppSettings default; meaningful on Windows but no
    /// better than any other default on Linux, so we migrate past it.</summary>
    private const string UpstreamDefaultHotkey = "Ctrl+Shift+F9";

    private static void ReconcileHotkeyOnStartup(HotkeyService hotkey, ISettingsService settings)
    {
        var s = settings.Current;
        hotkey.Mode = s.Mode;
        hotkey.TrySetPromptPaletteHotkeyFromString(s.PromptPaletteHotkey);
        hotkey.TrySetRecentTranscriptionsHotkeyFromString(s.RecentTranscriptionsHotkey);
        hotkey.TrySetCopyLastTranscriptionHotkeyFromString(s.CopyLastTranscriptionHotkey);
        hotkey.TrySetTransformSelectionHotkeyFromString(s.TransformSelectionHotkey);

        // Treat the upstream default as "unset" on Linux and substitute the
        // Linux default (HotkeyService's ctor-time binding — Ctrl+Shift+Space).
        // This prevents ApplyHotkey-on-SettingsChanged from silently rebinding
        // the hotkey to F9 when the user has never explicitly chosen a key.
        var linuxDefault = hotkey.CurrentHotkeyString;
        var persisted = s.ToggleHotkey;
        var shouldMigrate = string.IsNullOrWhiteSpace(persisted)
                            || persisted == UpstreamDefaultHotkey;

        if (shouldMigrate)
        {
            settings.Save(s with { ToggleHotkey = linuxDefault });
        }
        else if (!hotkey.TrySetHotkeyFromString(persisted))
        {
            // User-set but unparseable — keep the service default and fix
            // settings so UI/state agree.
            settings.Save(s with { ToggleHotkey = linuxDefault });
        }
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
            var sessionAudio = services.GetService<SessionAudioFileService>();
            sessionAudio?.DeleteSessionCaptures();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Session audio cleanup failed: {ex.Message}"); }

        try
        {
            var retention = services.GetService<HistoryRetentionCoordinator>();
            retention?.HandleShutdown();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] History retention shutdown failed: {ex.Message}"); }

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

        try
        {
            var playback = services.GetService<AudioPlaybackService>();
            playback?.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] Playback dispose failed: {ex.Message}"); }

        try
        {
            var api = services.GetService<HttpApiService>();
            api?.Dispose();
        }
        catch (Exception ex) { Debug.WriteLine($"[App] HTTP API dispose failed: {ex.Message}"); }

        await Task.CompletedTask;
    }

    private static async Task BootstrapAsync(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsService>();

        var history = services.GetRequiredService<IHistoryService>();
        await history.EnsureLoadedAsync();

        var sessionAudio = services.GetRequiredService<SessionAudioFileService>();
        sessionAudio.DeleteSessionCaptures();

        var audio = services.GetRequiredService<AudioRecordingService>();
        ApplyConfiguredMicrophone(audio, settings);

        var deployer = services.GetRequiredService<BundledPluginDeployer>();
        deployer.DeployIfMissing();

        var pluginManager = services.GetRequiredService<PluginManager>();
        await pluginManager.InitializeAsync();

        var pluginRegistry = services.GetRequiredService<PluginRegistryService>();
        _ = pluginRegistry.FirstRunAutoInstallAsync()
            .ContinueWith(_ => pluginRegistry.CheckForUpdatesAsync(), TaskScheduler.Default)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Debug.WriteLine($"[App] Plugin registry check failed: {t.Exception?.Message}");
            }, TaskScheduler.Default);

        var historyRetention = services.GetRequiredService<HistoryRetentionCoordinator>();
        historyRetention.Initialize();

        var modelManager = services.GetRequiredService<ModelManagerService>();
        modelManager.MigrateSettings();

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

    private static async Task BootstrapDeferredAsync(IServiceProvider services)
    {
        try
        {
            await BootstrapAsync(services);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Deferred bootstrap failed: {ex}");
        }
    }

    private static void ApplyConfiguredMicrophone(
        AudioRecordingService audio,
        ISettingsService settings)
    {
        var configuredIndex = settings.Current.SelectedMicrophoneDevice;
        var configuredId = settings.Current.SelectedMicrophoneDeviceId;
        if (!configuredIndex.HasValue && string.IsNullOrWhiteSpace(configuredId))
            return;

        try
        {
            var resolved = audio.ResolveConfiguredDevice(configuredIndex, configuredId);
            if (resolved is null)
                return;

            audio.SelectedDeviceIndex = resolved.Index;

            if (resolved.Index != configuredIndex || resolved.PersistentId != configuredId)
            {
                settings.Save(settings.Current with
                {
                    SelectedMicrophoneDevice = resolved.Index,
                    SelectedMicrophoneDeviceId = resolved.PersistentId
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Failed to restore microphone selection: {ex.Message}");
        }
    }
}
