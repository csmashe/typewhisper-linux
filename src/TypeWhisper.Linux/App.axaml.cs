using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net.Sockets;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Ipc;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux;

public class App : Application
{
    /// <summary>
    ///     Upstream AppSettings default; meaningful on Windows but no
    ///     better than any other default on Linux, so we migrate past it.
    /// </summary>
    private const string UpstreamDefaultHotkey = "Ctrl+Shift+F9";

    /// <summary>
    ///     Tray-menu Exit flips this; Close-button handler checks it to decide
    ///     whether to actually quit or hide to the tray. Access is UI-thread-only.
    /// </summary>
    private static bool ShuttingDown { get; set; }

    /// <summary>
    ///     While teardown runs, the Closing handler cancels closes (see the race
    ///     note there); ShutdownAndExitAsync sets this just before its final
    ///     desktop.Shutdown(). Access is UI-thread-only.
    /// </summary>
    private static bool ClosePermitted { get; set; }

    public override void Initialize()
    {
        BootTrace.Stage("App.Initialize begin");
        AvaloniaXamlLoader.Load(this);
        BootTrace.Stage("App.Initialize end");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        BootTrace.Stage("OnFrameworkInitializationCompleted begin");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = Program.Services;
            var settings = services.GetRequiredService<ISettingsService>();
            settings.Load();
            BootTrace.Stage("settings.Load");

            // Interface language: snapshot the real OS locale BEFORE any
            // override (so "Auto (System)" can restore it), load the JSON
            // catalogs, then apply the saved preference. Must run before
            // MainWindow is built so the first render is in the right language.
            Loc.SystemLanguage = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Loc.Instance.Initialize();
            Loc.Instance.CurrentLanguage = Loc.Instance.ResolveLanguage(settings.Current.UiLanguage);
            BootTrace.Stage("Loc.Initialize");

            // Reconcile configured state and verify native ownership before DictationOrchestrator
            // starts HotkeyService. This keeps the first backend snapshot free of a duplicate
            // app-owned dictation route when the current desktop spec is installed.
            var hotkey = services.GetRequiredService<HotkeyService>();
            ReconcileHotkeyOnStartup(hotkey, settings);
            var shortcuts = services.GetRequiredService<ShortcutsSectionViewModel>();
            using (var nativeBindingProbeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try
                {
                    shortcuts
                        .RefreshNativeDictationBindingStateAsync(nativeBindingProbeCts.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (OperationCanceledException ex)
                {
                    hotkey.SetNativeDictationBindingActive(false);
                    Trace.WriteLine($"[App] Native dictation binding probe timed out: {ex.Message}");
                }
                catch (Exception ex)
                {
                    hotkey.SetNativeDictationBindingActive(false);
                    Trace.WriteLine($"[App] Native dictation binding probe failed: {ex}");
                }
            }

            BootTrace.Stage("native dictation binding reconciled");

            // Tray must be initialized before MainWindow so IsTrayAvailable is set when
            // GeneralSection's close-to-tray binding latches (the probe raises no PropertyChanged).
            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            BootTrace.Stage("tray.Initialize");

            var main = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = main;
            BootTrace.Stage("MainWindow constructed");
            main.Opened += (_, _) => BootTrace.Stage("MainWindow.Opened fired");
            // We're up and on screen — end the desktop's "launching" busy
            // cursor. Avalonia never completes the startup-notification
            // sequence itself, so without this it spins until Mutter's timeout.
            main.Opened += (_, _) => LinuxStartupNotification.NotifyComplete();

            var prefs = services.GetRequiredService<LinuxPreferencesService>();

            // Close-button: CloseToTray+tray-available → hide; otherwise quit.
            // Hiding with no tray (stock GNOME) would strand the user with no UI.
            main.Closing += (sender, e) =>
            {
                if (ShuttingDown)
                {
                    // Teardown underway: keep canceling closes until ShutdownAndExitAsync
                    // sets ClosePermitted — on a tiling WM (overlay suppressed) this is the
                    // last window, so an early close would trip OnLastWindowClose and
                    // dispose DI while TearDownAsync is still running.
                    if (!ClosePermitted)
                    {
                        e.Cancel = true;
                    }

                    return;
                }

                if (prefs.Current.CloseToTray && tray.IsTrayAvailable)
                {
                    e.Cancel = true;
                    HideToTray(main);
                }
                else
                {
                    e.Cancel = true;
                    ShuttingDown = true;
                    _ = ShutdownAndExitAsync(services, desktop);
                }
            };

            tray.ShowSettingsRequested += (_, _) =>
            {
                ShowMainWindow(main);
                (
                    main.DataContext as MainWindowViewModel
                )?.Navigate<GeneralSectionViewModel>();
            };
            tray.ExitRequested += (_, _) =>
            {
                if (ShuttingDown)
                {
                    return;
                }

                ShuttingDown = true;
                _ = ShutdownAndExitAsync(services, desktop);
            };

            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();
            BootTrace.Stage("dictation.Initialize");

            var sessionResults = services.GetRequiredService<DictationSessionResultStore>();
            dictation.SessionCompleted += sessionResults.Record;

            // The bind doubles as the single-instance guard; AddressAlreadyInUse means
            // a live peer got here first — shut this instance down cleanly.
            var controlSocket = services.GetRequiredService<ControlSocketServer>();
            try
            {
                controlSocket.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.Error.WriteLine("TypeWhisper is already running.");
                ShuttingDown = true;
                // Window never opens on this path, so notify startup complete to clear
                // the GNOME launch cursor (main.Opened won't fire).
                LinuxStartupNotification.NotifyComplete();
                _ = ShutdownAndExitAsync(services, desktop);
                return;
            }
            catch (Exception ex)
            {
                // Non-fatal: continue without remote-toggle IPC.
                Debug.WriteLine($"[App] Control socket start failed: {ex.Message}");
            }

            BootTrace.Stage("controlSocket.Start");

            var overlay = services.GetRequiredService<DictationOverlayWindow>();
            overlay.Initialize();
            BootTrace.Stage("overlay.Initialize");

            // Surface silently-learned target-app corrections with an Undo. On desktop
            // environments this is a dedicated toast window placed beside the corrected element;
            // on tiling WMs the overlay/toast is the wrong primitive, so it goes out as a desktop
            // notification instead — each surface subscribes only in its own environment, so
            // exactly one owns CorrectionsLearned and it's never double-shown. Both marshal the
            // background commit event onto the UI thread internally. When the feature is off the
            // event never fires — neither starts anything on its own.
            if (DesktopDetector.UsesNotificationRecordingIndicator())
            {
                services.GetRequiredService<LearnedCorrectionsNotificationService>().Initialize();
            }
            else
            {
                services.GetRequiredService<LearnedCorrectionsToastController>().Initialize();
            }

            // On tiling window managers the overlay is suppressed (it's the wrong
            // primitive there); recording is surfaced via a desktop notification
            // instead. No-op on desktop environments, which keep the overlay.
            services.GetRequiredService<RecordingNotificationService>().Initialize();
            BootTrace.Stage("recordingNotification.Initialize");

            var errorLog = services.GetRequiredService<IErrorLogService>();
            var promptActions = services.GetRequiredService<IPromptActionService>();
            // Seed the disabled auto-cleanup prompt + profile on a first install,
            // before dynamic reconciliation reads them (both are disabled, so
            // their Ctrl+Alt+E binding stays inert until the user enables them).
            try
            {
                promptActions.SeedFirstRunDefaultsIfMissing();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[App] Failed to seed first-run prompt actions: {ex}");
                errorLog.AddEntry(
                    $"Could not seed first-run prompt actions: {ex.Message}",
                    ErrorCategory.Prompt
                );
            }

            var profileService = services.GetRequiredService<IProfileService>();
            profileService.SeedFirstRunDefaultsIfMissing();

            // ActionsChanged fires on the UI thread while ProfilesChanged can fire off the
            // HTTP worker thread (e.g. /v1/profiles/toggle), so the two subscriptions can enter
            // this reconcile concurrently. Serialize the snapshot-and-apply so a handler cannot
            // capture a stale view of the other service and revive a just-disabled binding.
            var reconcileLock = new object();

            void ReconcileDynamicHotkeys()
            {
                IReadOnlyList<string> rejections;
                lock (reconcileLock)
                {
                    rejections = hotkey.SetDynamicHotkeys(
                        HotkeyService.ParsePromptActionHotkeys(promptActions.Actions),
                        HotkeyService.ParseProfileHotkeys(profileService.Profiles)
                    );
                }

                foreach (var message in rejections)
                {
                    errorLog.AddEntry(message);
                }
            }

            ReconcileDynamicHotkeys();
            promptActions.ActionsChanged += ReconcileDynamicHotkeys;
            profileService.ProfilesChanged += ReconcileDynamicHotkeys;
            var lastApplied = hotkey.CurrentHotkeyString;
            var lastPromptPaletteApplied = hotkey.CurrentPromptPaletteHotkeyString;
            var lastRecentTranscriptionsApplied = hotkey.CurrentRecentTranscriptionsHotkeyString;
            var lastCopyLastTranscriptionApplied = hotkey.CurrentCopyLastTranscriptionHotkeyString;
            var lastTransformSelectionApplied = hotkey.CurrentTransformSelectionHotkeyString;
            settings.SettingsChanged += s =>
            {
                hotkey.Mode = s.Mode;
                if (
                    !string.IsNullOrWhiteSpace(s.ToggleHotkey)
                    && s.ToggleHotkey != lastApplied
                    && hotkey.TrySetHotkeyFromString(s.ToggleHotkey)
                )
                {
                    lastApplied = hotkey.CurrentHotkeyString;
                }

                if (
                    s.PromptPaletteHotkey != lastPromptPaletteApplied
                    && hotkey.TrySetPromptPaletteHotkeyFromString(s.PromptPaletteHotkey)
                )
                {
                    lastPromptPaletteApplied = hotkey.CurrentPromptPaletteHotkeyString;
                }

                if (
                    s.RecentTranscriptionsHotkey != lastRecentTranscriptionsApplied
                    && hotkey.TrySetRecentTranscriptionsHotkeyFromString(
                        s.RecentTranscriptionsHotkey
                    )
                )
                {
                    lastRecentTranscriptionsApplied =
                        hotkey.CurrentRecentTranscriptionsHotkeyString;
                }

                if (
                    s.CopyLastTranscriptionHotkey != lastCopyLastTranscriptionApplied
                    && hotkey.TrySetCopyLastTranscriptionHotkeyFromString(
                        s.CopyLastTranscriptionHotkey
                    )
                )
                {
                    lastCopyLastTranscriptionApplied =
                        hotkey.CurrentCopyLastTranscriptionHotkeyString;
                }

                if (
                    s.TransformSelectionHotkey != lastTransformSelectionApplied
                    && hotkey.TrySetTransformSelectionHotkeyFromString(s.TransformSelectionHotkey)
                )
                {
                    lastTransformSelectionApplied = hotkey.CurrentTransformSelectionHotkeyString;
                }
            };

            var api = services.GetRequiredService<HttpApiService>();
            api.ApplySettings();
            settings.SettingsChanged += _ => api.ApplySettings();

            var promptPalette = services.GetRequiredService<PromptPaletteService>();
            hotkey.PromptPaletteRequested += (_, _) => _ = promptPalette.TogglePaletteAsync();
            hotkey.PromptActionHotkeyTriggered += (_, actionId) =>
                FireAndForget(promptPalette.ExecuteActionDirectAsync(actionId));

            // Per-profile hotkeys. ProcessSelectedText runs the profile's
            // linked prompt action against the current selection; the dictation
            // variants force this profile through DictationOrchestrator's match.
            hotkey.ProfileTextProcessingRequested += (_, profileId) =>
            {
                var profile = profileService.Profiles.FirstOrDefault(p => p.Id == profileId);
                if (profile?.HotkeyBehavior != ProfileHotkeyBehavior.ProcessSelectedText)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(profile.PromptActionId))
                {
                    FireAndForget(promptPalette.ExecuteActionDirectAsync(profile.PromptActionId));
                }
                // else: nothing linked — no-op.
            };
            hotkey.ProfileDictationToggleRequested += (_, profileId) =>
                FireAndForget(dictation.ToggleAsync(profileId));
            hotkey.ProfileDictationStartRequested += (_, profileId) =>
                FireAndForget(dictation.StartAsync(profileId));
            hotkey.ProfileDictationStopRequested += (_, _) =>
                FireAndForget(dictation.StopAsync());

            var recentTranscriptions = services.GetRequiredService<RecentTranscriptionsService>();
            recentTranscriptions.FeedbackRequested += (message, isError) =>
            {
                Debug.WriteLine(
                    $"[RecentTranscriptions] {(isError ? "Error" : "Info")}: {message}"
                );
            };
            hotkey.RecentTranscriptionsRequested += (_, _) => recentTranscriptions.TogglePalette();
            hotkey.CopyLastTranscriptionRequested += (_, _) =>
                _ = recentTranscriptions.CopyLastTranscriptionToClipboardAsync();
            var transformSelection = services.GetRequiredService<TransformSelectionService>();
            hotkey.TransformSelectionRequested += (_, _) => _ = transformSelection.ToggleAsync();

            // Launch hidden to the tray if --minimized was passed.
            if (Program.StartMinimized)
            {
                main.Opened += (_, _) => HideToTray(main);
            }

            BootTrace.Stage("synchronous init complete; starting BootstrapDeferredAsync");
            var bootstrapTask = BootstrapDeferredAsync(services);

            // Detached from bootstrapTask so a slow update check can't delay
            // first-run onboarding (which awaits bootstrap below).
            _ = RunStartupUpdateCheckAsync(services);

            // First-run onboarding wizard. Wait for bootstrap so bundled
            // plugins are deployed and initialized before the model picker loads.
            if (!settings.Current.HasCompletedOnboarding)
            {
                main.Opened += async (_, _) =>
                {
                    await bootstrapTask;
                    (main.DataContext as MainWindowViewModel)?.OpenWizard();
                };
            }
        }

        BootTrace.Stage("OnFrameworkInitializationCompleted end (about to call base)");
        base.OnFrameworkInitializationCompleted();
        BootTrace.Stage("base.OnFrameworkInitializationCompleted returned");
    }

    private static void ReconcileHotkeyOnStartup(HotkeyService hotkey, ISettingsService settings)
    {
        var s = settings.Current;
        hotkey.Mode = s.Mode;
        hotkey.TrySetPromptPaletteHotkeyFromString(s.PromptPaletteHotkey);
        hotkey.TrySetRecentTranscriptionsHotkeyFromString(s.RecentTranscriptionsHotkey);
        hotkey.TrySetCopyLastTranscriptionHotkeyFromString(s.CopyLastTranscriptionHotkey);
        hotkey.TrySetTransformSelectionHotkeyFromString(s.TransformSelectionHotkey);

        // Treat the upstream Windows default ("Ctrl+Shift+F9") as unset on Linux and substitute
        // the Linux default (Ctrl+Shift+Space) to prevent SettingsChanged from rebinding to it.
        var linuxDefault = hotkey.CurrentHotkeyString;
        var persisted = s.ToggleHotkey;
        var shouldMigrate =
            string.IsNullOrWhiteSpace(persisted) || persisted == UpstreamDefaultHotkey;

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

    /// <summary>
    ///     True on wlroots compositors (Hyprland, Sway) where there is no minimize concept —
    ///     an iconify request is a no-op there, so Window.Hide()/Show() is used instead.
    ///     The unpainted-surface-on-Show() bug (backlog #3/#16) is Mutter-specific.
    /// </summary>
    private static bool TrayHideUsesWindowHide()
    {
        return DesktopDetector.DetectId() is "hyprland" or "sway";
    }

    /// <summary>
    ///     Hide the window to the tray. Compositor-dependent:
    ///     GNOME Mutter — minimize FIRST, then ShowInTaskbar=false (order matters: Mutter ignores
    ///     iconify on a window that already has SKIP_TASKBAR set). Keeps the surface mapped,
    ///     dodging the Hide()/Show() repaint bug (backlog #3/#16).
    ///     wlroots (Hyprland/Sway) — no minimize concept; use Window.Hide(), which repaints correctly.
    /// </summary>
    private static void HideToTray(MainWindow window)
    {
        if (TrayHideUsesWindowHide())
        {
            window.Hide();
            return;
        }

        window.WindowState = WindowState.Minimized;
        window.ShowInTaskbar = false;
    }

    private static void ShowMainWindow(MainWindow window)
    {
        // Reverse HideToTray: ShowInTaskbar=true first (Mutter won't un-iconify a skip-taskbar
        // window), then un-minimize or Show() depending on which path was used to hide.
        window.ShowInTaskbar = true;

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        // wlroots hide-to-tray uses a real Hide(); bring the surface back.
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.Activate();
        // GNOME Wayland may ignore an Activate() issued in the same UI turn
        // as the state change; re-issue it once the change has been applied.
        Dispatcher.UIThread.Post(window.Activate);
    }

    /// <summary>
    ///     Fire-and-forget a hotkey-triggered async action: starts the task and
    ///     never blocks the handler. Failures are logged rather than crashing the
    ///     app or vanishing as an unobserved task exception.
    /// </summary>
    private static void FireAndForget(Task task) =>
        task.ContinueWith(
            static t => Debug.WriteLine($"[App] Background hotkey action failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);

    private static async Task ShutdownAndExitAsync(
        IServiceProvider services,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await TearDownAsync(services);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Teardown failed during shutdown: {ex.Message}");
        }
        finally
        {
            ClosePermitted = true;
            // Must call Shutdown explicitly: DictationOverlayWindow is always-shown
            // (backlog #16 Opacity workaround) so OnLastWindowClose never fires.
            desktop.Shutdown();
        }
    }

    /// <summary>
    ///     Best-effort ordered shutdown of services that own native threads.
    ///     Runs before desktop.Shutdown() so the Host isn't left racing
    ///     libuiohook / PortAudio on exit.
    /// </summary>
    private static async Task TearDownAsync(IServiceProvider services)
    {
        try
        {
            services.GetService<SessionAudioFileService>()?.DeleteSessionCaptures();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Session audio cleanup failed: {ex.Message}");
        }

        try
        {
            var retention = services.GetService<HistoryRetentionCoordinator>();
            retention?.HandleShutdown();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] History retention shutdown failed: {ex.Message}");
        }

        try
        {
            var hotkey = services.GetService<HotkeyService>();
            hotkey?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Hotkey dispose failed: {ex.Message}");
        }

        try
        {
            var controlSocket = services.GetService<ControlSocketServer>();
            controlSocket?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Control socket dispose failed: {ex.Message}");
        }

        try
        {
            var tray = services.GetService<TrayIconService>();
            tray?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Tray dispose failed: {ex.Message}");
        }

        try
        {
            var models = services.GetService<ModelManagerService>();
            if (models is not null)
            {
                await models.UnloadModelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Model unload failed: {ex.Message}");
        }

        try
        {
            var audio = services.GetService<AudioRecordingService>();
            audio?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Audio dispose failed: {ex.Message}");
        }

        try
        {
            var playback = services.GetService<AudioPlaybackService>();
            playback?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Playback dispose failed: {ex.Message}");
        }

        try
        {
            var api = services.GetService<HttpApiService>();
            api?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] HTTP API dispose failed: {ex.Message}");
        }

        try
        {
            var sessionResults = services.GetService<DictationSessionResultStore>();
            sessionResults?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Dictation session result store dispose failed: {ex.Message}");
        }
    }

    private static async Task BootstrapAsync(IServiceProvider services)
    {
        BootTrace.Stage("BootstrapAsync begin");
        var settings = services.GetRequiredService<ISettingsService>();

        var history = services.GetRequiredService<IHistoryService>();
        await history.EnsureLoadedAsync();
        BootTrace.Stage("history.EnsureLoadedAsync");

        services.GetRequiredService<SessionAudioFileService>().DeleteSessionCaptures();

        var audio = services.GetRequiredService<AudioRecordingService>();
        ApplyConfiguredMicrophone(audio, settings);
        BootTrace.Stage("audio configured");

        _ = services.GetRequiredService<BundledPluginDeployer>();
        BundledPluginDeployer.DeployIfMissing();
        BootTrace.Stage("BundledPluginDeployer.DeployIfMissing");

        var pluginManager = services.GetRequiredService<PluginManager>();
        await pluginManager.InitializeAsync();
        BootTrace.Stage("PluginManager.InitializeAsync");

        // PluginRegistryService targets the upstream Windows registry (Windows-built artifacts);
        // the Linux fork ships its own plugins via BundledPluginDeployer, so the registry is not used.

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

    /// <summary>
    ///     Once-per-day GitHub release check. Detached from the bootstrap task so a slow/
    ///     unreachable network doesn't delay first-run onboarding (which awaits bootstrap).
    ///     Failures are swallowed inside the service; a found update drives the banner via
    ///     <c>UpdateCheckService.ResultChanged</c>.
    /// </summary>
    private static async Task RunStartupUpdateCheckAsync(IServiceProvider services)
    {
        try
        {
            var updateCheck = services.GetRequiredService<UpdateCheckService>();
            await updateCheck.CheckOnStartupAsync();
            BootTrace.Stage("UpdateCheckService.CheckOnStartupAsync");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Startup update check failed: {ex.Message}");
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
        ISettingsService settings
    )
    {
        var configuredIndex = settings.Current.SelectedMicrophoneDevice;
        var configuredId = settings.Current.SelectedMicrophoneDeviceId;
        if (!configuredIndex.HasValue && string.IsNullOrWhiteSpace(configuredId))
        {
            return;
        }

        try
        {
            var resolved = audio.ResolveConfiguredDevice(configuredIndex, configuredId);
            if (resolved is null)
            {
                return;
            }

            audio.SelectedDeviceIndex = resolved.Index;

            if (resolved.Index != configuredIndex || resolved.PersistentId != configuredId)
            {
                settings.Save(
                    settings.Current with
                    {
                        SelectedMicrophoneDevice = resolved.Index,
                        SelectedMicrophoneDeviceId = resolved.PersistentId
                    }
                );
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Failed to restore microphone selection: {ex.Message}");
        }
    }
}
