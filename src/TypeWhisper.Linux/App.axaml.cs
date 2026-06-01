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
    ///     whether to actually quit or hide to the tray.
    /// </summary>
    public static bool ShuttingDown { get; private set; }

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

            // Resolve + initialize the tray before MainWindow is built. The
            // probe sets TrayIconService.IsTrayAvailable, which GeneralSection
            // binds to (the close-to-tray toggle, backlog #18). Building the
            // window first would let that binding latch the default `false`
            // and — the one-shot probe raises no PropertyChanged — keep the
            // toggle wrongly disabled all session on a machine with a tray.
            // It also has to be ready before the close handler is wired.
            var tray = services.GetRequiredService<TrayIconService>();
            tray.Initialize();
            BootTrace.Stage("tray.Initialize");

            var main = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = main;
            BootTrace.Stage("MainWindow constructed");
            main.Opened += (_, _) => BootTrace.Stage("MainWindow.Opened fired");

            var prefs = services.GetRequiredService<LinuxPreferencesService>();

            // Close-button behavior is user-configurable. Default
            // (CloseToTray=false): X fully quits, same path as tray Exit.
            // With CloseToTray=true the window hides and the tray stays the
            // entry point. Tray Exit always quits (flips ShuttingDown first).
            main.Closing += (_, e) =>
            {
                if (ShuttingDown)
                {
                    return;
                }

                // Only hide to the tray when one actually exists to restore
                // the window from. Hiding with no tray (stock GNOME has none)
                // would strand the user with no UI — backlog #18 — so an X
                // with no tray falls through to quitting.
                if (prefs.Current.CloseToTray && tray.IsTrayAvailable)
                {
                    e.Cancel = true;
                    HideToTray(main);
                }
                else
                {
                    ShuttingDown = true;
                    TearDownAsync(services).GetAwaiter().GetResult();
                    // Shut the lifetime down explicitly. The default
                    // OnLastWindowClose mode would leave the process alive:
                    // DictationOverlayWindow is a persistent always-shown
                    // window (backlog #16's Opacity workaround), so closing
                    // the main window is never the *last* window close.
                    // Same path as the tray Exit handler.
                    desktop.Shutdown();
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
                ShuttingDown = true;
                TearDownAsync(services).GetAwaiter().GetResult();
                desktop.Shutdown();
            };

            var dictation = services.GetRequiredService<DictationOrchestrator>();
            dictation.Initialize();
            tray.DictationToggleRequested += (_, _) => _ = dictation.ToggleAsync();
            BootTrace.Stage("dictation.Initialize");

            var sessionResults = services.GetRequiredService<DictationSessionResultStore>();
            dictation.SessionCompleted += sessionResults.Record;

            // Bring up the IPC control socket so `typewhisper` (with no args)
            // from a second terminal can toggle dictation in this instance.
            // The bind itself is the single-instance guard; if another live
            // peer beat us to it we shut this instance back down cleanly so
            // the user doesn't end up with two trays/orchestrators.
            var controlSocket = services.GetRequiredService<ControlSocketServer>();
            try
            {
                controlSocket.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.Error.WriteLine("TypeWhisper is already running.");
                ShuttingDown = true;
                TearDownAsync(services).GetAwaiter().GetResult();
                desktop.Shutdown();
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
            var promptActions = services.GetRequiredService<IPromptActionService>();
            // Seed the disabled auto-cleanup prompt + profile on a first install,
            // before the hotkey snapshots below read them (both are disabled, so
            // their Ctrl+Alt+E binding stays inert until the user enables them).
            promptActions.SeedFirstRunDefaultsIfMissing();
            hotkey.SetPromptActionHotkeys(
                HotkeyService.ParsePromptActionHotkeys(promptActions.Actions)
            );
            promptActions.ActionsChanged += () =>
                hotkey.SetPromptActionHotkeys(
                    HotkeyService.ParsePromptActionHotkeys(promptActions.Actions)
                );
            var profileService = services.GetRequiredService<IProfileService>();
            profileService.SeedFirstRunDefaultsIfMissing();
            hotkey.SetProfileHotkeys(
                HotkeyService.ParseProfileHotkeys(profileService.Profiles)
            );
            profileService.ProfilesChanged += () =>
                hotkey.SetProfileHotkeys(
                    HotkeyService.ParseProfileHotkeys(profileService.Profiles)
                );
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
                _ = promptPalette.ExecuteActionDirectAsync(actionId);

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
                    _ = promptPalette.ExecuteActionDirectAsync(profile.PromptActionId);
                }
                // else: nothing linked — no-op.
            };
            hotkey.ProfileDictationToggleRequested += (_, profileId) =>
                _ = dictation.ToggleAsync(profileId);
            hotkey.ProfileDictationStartRequested += (_, profileId) =>
                _ = dictation.StartAsync(profileId);
            hotkey.ProfileDictationStopRequested += (_, _) => _ = dictation.StopAsync();

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

        // Treat the upstream default as "unset" on Linux and substitute the
        // Linux default (HotkeyService's ctor-time binding — Ctrl+Shift+Space).
        // This prevents ApplyHotkey-on-SettingsChanged from silently rebinding
        // the hotkey to F9 when the user has never explicitly chosen a key.
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
    ///     True on wlroots-based compositors (Hyprland, Sway), which have no
    ///     "minimize" concept — an X11 iconify request is a no-op there, so the
    ///     minimize-based hide-to-tray would leave the window on screen. wlroots
    ///     does handle Window.Hide()/Show() cleanly; the unpainted-surface-on-
    ///     Show() bug behind backlog #3/#16 is specific to GNOME Mutter.
    /// </summary>
    private static bool TrayHideUsesWindowHide()
    {
        return DesktopDetector.DetectId() is "hyprland" or "sway";
    }

    /// <summary>
    ///     Hide the window to the tray so the tray icon becomes the only entry
    ///     point. Compositor-dependent — no single mechanism both removes the
    ///     window from screen *and* from the dock everywhere:
    ///     <list type="bullet">
    ///         <item>
    ///             GNOME Mutter (and other minimize-honoring desktops): minimize
    ///             FIRST, then ShowInTaskbar=false. Mutter ignores an iconify request on
    ///             a window that already has SKIP_TASKBAR set (minimize means "send it to
    ///             the dash," and a skip-taskbar window has nowhere to go), so the order
    ///             matters. Keeps the surface mapped, dodging the Hide()/Show() repaint
    ///             bug — backlog #3/#16.
    ///         </item>
    ///         <item>
    ///             wlroots (Hyprland/Sway): no minimize concept, so a real
    ///             Window.Hide() — which repaints correctly on Show() there.
    ///         </item>
    ///     </list>
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
        // Reverse HideToTray. The hide path is compositor-dependent, so
        // restore covers both: re-add the dock entry, un-minimize if we
        // minimized (GNOME — and re-add the dock entry FIRST, since Mutter
        // won't un-iconify a skip-taskbar window), and Show() if we Hid()
        // (wlroots).
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
    ///     Best-effort ordered shutdown of services that own native threads.
    ///     Runs before desktop.Shutdown() so the Host isn't left racing
    ///     libuiohook / PortAudio on exit.
    /// </summary>
    private static async Task TearDownAsync(IServiceProvider services)
    {
        try
        {
            var sessionAudio = services.GetService<SessionAudioFileService>();
            sessionAudio?.DeleteSessionCaptures();
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
            models?.UnloadModel();
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

        // Placeholder: keeps the method async for future awaitable teardown
        // steps without forcing callers to change the signature.
        await Task.CompletedTask;
    }

    private static async Task BootstrapAsync(IServiceProvider services)
    {
        BootTrace.Stage("BootstrapAsync begin");
        var settings = services.GetRequiredService<ISettingsService>();

        var history = services.GetRequiredService<IHistoryService>();
        await history.EnsureLoadedAsync();
        BootTrace.Stage("history.EnsureLoadedAsync");

        var sessionAudio = services.GetRequiredService<SessionAudioFileService>();
        sessionAudio.DeleteSessionCaptures();

        var audio = services.GetRequiredService<AudioRecordingService>();
        ApplyConfiguredMicrophone(audio, settings);
        BootTrace.Stage("audio configured");

        var deployer = services.GetRequiredService<BundledPluginDeployer>();
        deployer.DeployIfMissing();
        BootTrace.Stage("BundledPluginDeployer.DeployIfMissing");

        var pluginManager = services.GetRequiredService<PluginManager>();
        await pluginManager.InitializeAsync();
        BootTrace.Stage("PluginManager.InitializeAsync");

        // The remote plugin registry (PluginRegistryService) targets the
        // upstream Windows registry, which serves Windows-built plugin
        // artifacts. The Linux fork ships its own rewritten plugins via
        // BundledPluginDeployer above, so the registry's first-run
        // auto-install and update check are intentionally not run here.

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
    ///     Fire-and-forget once-per-day GitHub release check. Kept off the
    ///     bootstrap task on purpose: first-run onboarding awaits bootstrap, and
    ///     update checking is unrelated to the model/plugin setup the wizard
    ///     needs, so a slow or unreachable network must not delay the wizard by
    ///     the HTTP timeout. Network failures are swallowed inside the service;
    ///     a found update drives the main window's banner via
    ///     UpdateCheckService.ResultChanged (already subscribed by the VMs).
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