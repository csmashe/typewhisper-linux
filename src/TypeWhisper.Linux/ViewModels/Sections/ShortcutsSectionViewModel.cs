using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ShortcutsSectionViewModel : ObservableObject
{
    private const string AddToInputGroupCommand = "sudo usermod -aG input $USER";
    private const string DictationShortcutId = "typewhisper.dictation.toggle";
    private const string DictationDisplayName = "TypeWhisper: Toggle Dictation";

    private readonly HotkeyService _hotkey;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<IDeShortcutWriter> _writers;

    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _promptPaletteHotkeyText = "";
    [ObservableProperty] private string _recentTranscriptionsHotkeyText = "";
    [ObservableProperty] private string _copyLastTranscriptionHotkeyText = "";
    [ObservableProperty] private string _transformSelectionHotkeyText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _integrationStatusMessage = "";
    [ObservableProperty] private RecordingMode _mode;
    [ObservableProperty] private bool _waylandEvdevHotkeysEnabled;

    public IReadOnlyList<RecordingMode> Modes { get; } =
        [RecordingMode.Toggle, RecordingMode.PushToTalk, RecordingMode.Hybrid];

    /// <summary>Stable id of the currently-active backend, e.g. <c>linux-evdev</c>.</summary>
    public string ActiveBackendId => _hotkey.ActiveBackendId ?? "(not initialized)";

    /// <summary>Human-readable backend name shown in the status panel.</summary>
    public string ActiveBackendDisplayName => _hotkey.ActiveBackendDisplayName ?? "(not initialized)";

    /// <summary>Session type ("wayland", "x11", or "unknown").</summary>
    public string SessionType => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";

    /// <summary>True if the active backend can deliver press/release pairs.</summary>
    public bool SupportsPressRelease => _hotkey.ActiveBackendSupportsPressRelease ?? false;

    /// <summary>
    /// Human-readable capture scope shown in the status panel. Distinct
    /// from <see cref="SupportsPressRelease"/> because SharpHook on
    /// Wayland delivers press+release but only while TypeWhisper has focus
    /// — labeling that as "Global" would mislead users diagnosing a
    /// broken hotkey.
    /// </summary>
    public string ScopeText
    {
        get
        {
            var global = _hotkey.ActiveBackendIsGlobalScope;
            if (global is null) return "(not initialized)";
            return global.Value ? "Global (works in any focused window)" : "Focused only (TypeWhisper window)";
        }
    }

    /// <summary>
    /// True when the active backend can only deliver presses but the user
    /// has selected a mode that requires release events (PTT / Hybrid).
    /// Drives the capability-mismatch warning in the UI.
    /// </summary>
    public bool ShowCapabilityMismatch =>
        _hotkey.BackendRequiresToggleMode && Mode != RecordingMode.Toggle;

    /// <summary>
    /// True when the user is on a Wayland session and not a member of the
    /// <c>input</c> group — i.e. the evdev backend can't activate until they
    /// run the suggested usermod command. Null result from the group check
    /// (e.g. /proc not available) hides the banner.
    /// </summary>
    public bool ShowInputGroupBanner
    {
        get
        {
            if (!IsWaylandSession()) return false;
            var inGroup = InputGroupCheck.CurrentUserInInputGroup();
            return inGroup == false;
        }
    }

    public string InputGroupCommand => AddToInputGroupCommand;

    /// <summary>
    /// Command users paste into a DE shortcut binding to toggle dictation
    /// from any focused window. The bare binary name relies on Phase 4's
    /// single-instance IPC: a second invocation toggles the existing
    /// instance instead of launching a new one.
    /// </summary>
    public string CustomShortcutCommand => "typewhisper";

    /// <summary>
    /// True when the detected desktop supports separate press/release binds
    /// (Hyprland's <c>bind</c>+<c>bindr</c>, Sway's <c>bindsym</c> +
    /// <c>--release</c>). When true the UI shows a two-line PTT snippet
    /// alongside the toggle command; when false only the toggle snippet is
    /// shown.
    /// </summary>
    public bool ShowPushToTalkSnippet => DesktopName is "Hyprland" or "Sway";

    /// <summary>
    /// First line of the desktop-specific push-to-talk binding snippet
    /// (key press → <c>record start</c>). Empty for desktops that don't
    /// support the press/release pair.
    /// </summary>
    public string PushToTalkPressSnippet => DesktopName switch
    {
        // Hyprland: `bind` fires on press. CTRL+SHIFT+SPACE is the same
        // default suggested elsewhere in the UI so users don't have to
        // pick a key just to read the example.
        "Hyprland" => "bind  = CTRL SHIFT, SPACE, exec, typewhisper record start",
        // Sway: `--no-repeat` prevents the press-bind from auto-repeating
        // while the key is held, which would otherwise hammer record.start
        // many times per second. The orchestrator is idempotent so this is
        // safe, but the noise is unhelpful.
        "Sway"     => "bindsym --no-repeat $mod+space exec typewhisper record start",
        _          => "",
    };

    /// <summary>
    /// Second line of the push-to-talk binding snippet (key release →
    /// <c>record stop</c>). Empty for desktops without press/release.
    /// </summary>
    public string PushToTalkReleaseSnippet => DesktopName switch
    {
        "Hyprland" => "bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop",
        "Sway"     => "bindsym --release $mod+space exec typewhisper record stop",
        _          => "",
    };

    /// <summary>
    /// Short label describing what the PTT snippet does, surfaced above the
    /// command lines so a user copying the snippet knows they're getting
    /// true hold-to-talk rather than the toggle shown elsewhere on the page.
    /// </summary>
    public string PushToTalkSnippetHint => DesktopName switch
    {
        "Hyprland" => "Hyprland supports separate press/release binds. Use this pair for true push-to-talk:",
        "Sway"     => "Sway supports a press/release pair. Use these two binds for true push-to-talk:",
        _          => "",
    };

    /// <summary>
    /// Best-effort desktop name. Routed through
    /// <see cref="DesktopDetector"/> so the snippet-display string and
    /// the writer-selection logic agree on what "Hyprland" or "GNOME"
    /// means — previously the VM and the new writers each parsed the
    /// env var independently and could disagree on edge cases like
    /// "ubuntu:GNOME". The trailing "KDE Plasma" → "KDE" tweak keeps
    /// the legacy snippet-display keys intact.
    /// </summary>
    public string DesktopName
    {
        get
        {
            var name = DesktopDetector.DisplayName();
            return name == "KDE Plasma" ? "KDE" : name;
        }
    }

    /// <summary>
    /// DE-tailored instructions for binding a global shortcut to the
    /// <c>typewhisper</c> command. Falls back to generic wording for
    /// unknown desktops.
    /// </summary>
    public string DesktopInstructions => DesktopName switch
    {
        "GNOME" =>
            "Open Settings → Keyboard → View and Customize Shortcuts → Custom Shortcuts.\n" +
            "Add a new entry, paste the command above, and pick the keys you want.",
        "KDE" =>
            "Open System Settings → Shortcuts → Custom Shortcuts.\n" +
            "Edit → New → Global Shortcut → Command/URL, paste the command above, and assign a trigger.",
        "Hyprland" =>
            "Edit ~/.config/hypr/hyprland.conf and add a bind line, e.g.:\n" +
            "  bind = SUPER, SPACE, exec, typewhisper\n" +
            "Reload with `hyprctl reload`.",
        "Sway" =>
            "Edit ~/.config/sway/config and add a bindsym, e.g.:\n" +
            "  bindsym $mod+space exec typewhisper\n" +
            "Reload with `swaymsg reload`.",
        "XFCE" =>
            "Open Settings → Keyboard → Application Shortcuts → Add.\n" +
            "Paste the command above and choose the key combination when prompted.",
        "Cinnamon" =>
            "Open System Settings → Keyboard → Shortcuts → Custom Shortcuts.\n" +
            "Add a custom shortcut with the command above and bind a key.",
        "MATE" =>
            "Open System Settings → Keyboard Shortcuts → Add.\n" +
            "Paste the command above and assign a key combination.",
        _ =>
            "Open your desktop's keyboard settings and add a custom shortcut that runs the command above.\n" +
            "Bind it to any key combination you like (e.g. Ctrl+Shift+Space).",
    };

    /// <summary>
    /// Test-friendly constructor — defaults the writer list to empty
    /// so unit tests that don't care about DE integration don't need
    /// to fabricate writers. Production wiring passes the registered
    /// <see cref="IDeShortcutWriter"/> collection from DI.
    /// </summary>
    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
        : this(hotkey, settings, Array.Empty<IDeShortcutWriter>())
    {
    }

    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings, IEnumerable<IDeShortcutWriter> writers)
    {
        _hotkey = hotkey;
        _settings = settings;
        _writers = writers.ToArray();
        HotkeyText = _hotkey.CurrentHotkeyString;
        PromptPaletteHotkeyText = settings.Current.PromptPaletteHotkey;
        RecentTranscriptionsHotkeyText = settings.Current.RecentTranscriptionsHotkey;
        CopyLastTranscriptionHotkeyText = settings.Current.CopyLastTranscriptionHotkey;
        TransformSelectionHotkeyText = settings.Current.TransformSelectionHotkey;
        Mode = settings.Current.Mode;
        _waylandEvdevHotkeysEnabled = settings.Current.WaylandEvdevHotkeysEnabled;
    }

    /// <summary>
    /// First writer whose <see cref="IDeShortcutWriter.IsCurrentDesktop"/>
    /// returns true, or null when we're on an unsupported DE / when no
    /// writers were registered (e.g. unit tests). Cached lazily so the
    /// IsCurrentDesktop check doesn't re-run for every property the UI
    /// binds. The detection is cheap but the env-var-free path
    /// (BinaryExists) does touch the filesystem.
    /// </summary>
    private IDeShortcutWriter? _activeWriterCache;
    private bool _activeWriterCached;
    private IDeShortcutWriter? ActiveWriter
    {
        get
        {
            if (_activeWriterCached) return _activeWriterCache;
            _activeWriterCached = true;
            foreach (var w in _writers)
            {
                try
                {
                    if (w.IsCurrentDesktop()) { _activeWriterCache = w; break; }
                }
                catch
                {
                    // A buggy writer must never break the Shortcuts panel.
                }
            }
            return _activeWriterCache;
        }
    }

    /// <summary>True when a writer matches the current DE and the
    /// "Set up automatically" button should be shown.</summary>
    public bool CanSetupAutomatically => ActiveWriter is not null;

    /// <summary>"Set up automatically (GNOME)" — DE name baked in so
    /// the button is unambiguous when shown next to the "Show me the
    /// commands" alternative.</summary>
    public string SetupAutomaticallyLabel =>
        ActiveWriter is null ? "Set up automatically" : $"Set up automatically ({ActiveWriter.DisplayName})";

    /// <summary>Preview of the lines the active writer would change,
    /// shown beside the auto-setup button so the user can audit what
    /// the click will do before they click it.</summary>
    public string IntegrationPreview
    {
        get
        {
            var w = ActiveWriter;
            if (w is null) return string.Empty;
            return w.PreviewLines(BuildSpec(w));
        }
    }

    /// <summary>
    /// Build the <see cref="DeShortcutSpec"/> we hand to the writer.
    /// Defaults the trigger from the user's currently-configured
    /// toggle hotkey so they don't have to re-enter it — and for PTT-
    /// capable DEs we emit the record start/stop/cancel triplet so the
    /// installed bind drives the Phase 5 CLI directly.
    /// </summary>
    private DeShortcutSpec BuildSpec(IDeShortcutWriter writer)
    {
        var trigger = string.IsNullOrWhiteSpace(_settings.Current.ToggleHotkey)
            ? "Ctrl+Shift+Space"
            : _settings.Current.ToggleHotkey;
        if (writer.SupportsPushToTalk)
        {
            return new DeShortcutSpec(
                DictationShortcutId,
                DictationDisplayName,
                trigger,
                "typewhisper record start",
                "typewhisper record stop",
                // Cancel mirrors the trigger but swaps Space → Escape.
                // It only fires when the user has configured a cancel
                // accelerator; we synthesize a reasonable default for
                // them rather than asking up-front.
                SwapKeyForCancel(trigger),
                "typewhisper record cancel");
        }

        return new DeShortcutSpec(
            DictationShortcutId,
            DictationDisplayName,
            trigger,
            "typewhisper",
            null, null, null);
    }

    private static string SwapKeyForCancel(string trigger)
    {
        // Replace just the terminal key — the modifier stack stays
        // identical so the cancel binding can't accidentally collide
        // with the start binding by virtue of being too similar.
        var parts = trigger.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "Ctrl+Shift+Escape";
        parts[^1] = "Escape";
        return string.Join('+', parts);
    }

    /// <summary>
    /// Raised when the user clicks the "Copy" button next to the custom
    /// shortcut command. The view subscribes and performs the actual
    /// clipboard write via the parent <see cref="Avalonia.Controls.TopLevel"/>
    /// — VMs don't have direct access to the clipboard in Avalonia.
    /// </summary>
    public event EventHandler<string>? CopyCustomShortcutRequested;

    private static bool IsWaylandSession() =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
            StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ApplyRecentTranscriptionsHotkey()
    {
        if (_hotkey.TrySetRecentTranscriptionsHotkeyFromString(RecentTranscriptionsHotkeyText))
        {
            _settings.Save(_settings.Current with
            {
                RecentTranscriptionsHotkey = _hotkey.CurrentRecentTranscriptionsHotkeyString
            });
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentRecentTranscriptionsHotkeyString)
                ? "Recent transcriptions hotkey cleared."
                : $"Recent transcriptions hotkey set to {_hotkey.CurrentRecentTranscriptionsHotkeyString}.";
            RecentTranscriptionsHotkeyText = _hotkey.CurrentRecentTranscriptionsHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{RecentTranscriptionsHotkeyText}' or it collides with another shortcut.";
        }
    }

    [RelayCommand]
    private void ApplyCopyLastTranscriptionHotkey()
    {
        if (_hotkey.TrySetCopyLastTranscriptionHotkeyFromString(CopyLastTranscriptionHotkeyText))
        {
            _settings.Save(_settings.Current with
            {
                CopyLastTranscriptionHotkey = _hotkey.CurrentCopyLastTranscriptionHotkeyString
            });
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentCopyLastTranscriptionHotkeyString)
                ? "Copy last transcription hotkey cleared."
                : $"Copy last transcription hotkey set to {_hotkey.CurrentCopyLastTranscriptionHotkeyString}.";
            CopyLastTranscriptionHotkeyText = _hotkey.CurrentCopyLastTranscriptionHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{CopyLastTranscriptionHotkeyText}' or it collides with another shortcut.";
        }
    }

    [RelayCommand]
    private void ApplyTransformSelectionHotkey()
    {
        if (_hotkey.TrySetTransformSelectionHotkeyFromString(TransformSelectionHotkeyText))
        {
            _settings.Save(_settings.Current with
            {
                TransformSelectionHotkey = _hotkey.CurrentTransformSelectionHotkeyString
            });
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentTransformSelectionHotkeyString)
                ? "Transform selection hotkey cleared."
                : $"Transform selection hotkey set to {_hotkey.CurrentTransformSelectionHotkeyString}.";
            TransformSelectionHotkeyText = _hotkey.CurrentTransformSelectionHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{TransformSelectionHotkeyText}' or it collides with another shortcut.";
        }
    }

    [RelayCommand]
    private async Task SetupAutomaticallyAsync()
    {
        var writer = ActiveWriter;
        if (writer is null)
        {
            IntegrationStatusMessage = "No automatic setup is available for this desktop.";
            return;
        }
        IntegrationStatusMessage = $"Installing shortcut on {writer.DisplayName}…";
        try
        {
            var result = await writer.WriteAsync(BuildSpec(writer), CancellationToken.None).ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = $"Setup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveIntegrationAsync()
    {
        var writer = ActiveWriter;
        if (writer is null)
        {
            IntegrationStatusMessage = "No automatic setup is available for this desktop.";
            return;
        }
        IntegrationStatusMessage = $"Removing shortcut from {writer.DisplayName}…";
        try
        {
            var result = await writer.RemoveAsync(DictationShortcutId, CancellationToken.None).ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = $"Removal failed: {ex.Message}";
        }
    }

    private static string FormatResultMessage(DeShortcutWriteResult result)
    {
        var prefix = result.Success ? "" : "Could not finish: ";
        var msg = string.IsNullOrWhiteSpace(result.UserMessage) ? (result.Success ? "Done." : "Unknown error.") : result.UserMessage;
        return string.IsNullOrWhiteSpace(result.Warning)
            ? prefix + msg
            : $"{prefix}{msg} ({result.Warning})";
    }

    [RelayCommand]
    private void CopyCustomShortcut()
    {
        CopyCustomShortcutRequested?.Invoke(this, CustomShortcutCommand);
        StatusMessage = $"Copied '{CustomShortcutCommand}' to the clipboard.";
    }

    [RelayCommand]
    private void CopyPushToTalkPressSnippet()
    {
        if (string.IsNullOrEmpty(PushToTalkPressSnippet)) return;
        CopyCustomShortcutRequested?.Invoke(this, PushToTalkPressSnippet);
        StatusMessage = "Copied press bind to the clipboard.";
    }

    [RelayCommand]
    private void CopyPushToTalkReleaseSnippet()
    {
        if (string.IsNullOrEmpty(PushToTalkReleaseSnippet)) return;
        CopyCustomShortcutRequested?.Invoke(this, PushToTalkReleaseSnippet);
        StatusMessage = "Copied release bind to the clipboard.";
    }

    [RelayCommand]
    private void CopyPushToTalkPair()
    {
        if (!ShowPushToTalkSnippet) return;
        var combined = $"{PushToTalkPressSnippet}\n{PushToTalkReleaseSnippet}";
        CopyCustomShortcutRequested?.Invoke(this, combined);
        StatusMessage = "Copied push-to-talk binds to the clipboard.";
    }

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (_hotkey.TrySetHotkeyFromString(HotkeyText))
        {
            _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            StatusMessage = $"Hotkey set to {_hotkey.CurrentHotkeyString}.";
            HotkeyText = _hotkey.CurrentHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{HotkeyText}'. Try e.g. Ctrl+Shift+Space, Alt+F9, Ctrl+K.";
        }
    }

    [RelayCommand]
    private void ApplyPromptPaletteHotkey()
    {
        if (_hotkey.TrySetPromptPaletteHotkeyFromString(PromptPaletteHotkeyText))
        {
            _settings.Save(_settings.Current with
            {
                PromptPaletteHotkey = _hotkey.CurrentPromptPaletteHotkeyString
            });
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentPromptPaletteHotkeyString)
                ? "Prompt palette hotkey cleared."
                : $"Prompt palette hotkey set to {_hotkey.CurrentPromptPaletteHotkeyString}.";
            PromptPaletteHotkeyText = _hotkey.CurrentPromptPaletteHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{PromptPaletteHotkeyText}'. Try e.g. Ctrl+Shift+P, Alt+F10, Ctrl+K.";
        }
    }

    partial void OnModeChanged(RecordingMode value)
    {
        if (_settings.Current.Mode == value) return;
        _settings.Save(_settings.Current with { Mode = value });
        StatusMessage = value switch
        {
            RecordingMode.Toggle => "Press the hotkey to start, press again to stop.",
            RecordingMode.PushToTalk => "Hold the hotkey to record; release to stop and transcribe.",
            RecordingMode.Hybrid => "Starts immediately. Short press keeps recording; hold past ~600 ms stops on release.",
            _ => "",
        };
        OnPropertyChanged(nameof(ShowCapabilityMismatch));
    }

    partial void OnWaylandEvdevHotkeysEnabledChanged(bool value)
    {
        if (_settings.Current.WaylandEvdevHotkeysEnabled == value) return;
        _settings.Save(_settings.Current with { WaylandEvdevHotkeysEnabled = value });
        StatusMessage = value
            ? "Global keyboard reads enabled."
            : "Falling back to focused-only hotkeys.";

        // Hot-swap immediately so disabling actually stops the evdev
        // reader — a delayed (restart-only) opt-out is a real consent gap
        // for a setting that controls global keyboard event access.
        var task = SwitchBackendAndNotifyAsync();
        task.ContinueWith(
            t => StatusMessage = $"Backend switch failed: {t.Exception?.GetBaseException().Message}",
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task SwitchBackendAndNotifyAsync()
    {
        try
        {
            await _hotkey.SwitchBackendAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backend switch failed: {ex.Message}";
            return;
        }
        OnPropertyChanged(nameof(ActiveBackendId));
        OnPropertyChanged(nameof(ActiveBackendDisplayName));
        OnPropertyChanged(nameof(SupportsPressRelease));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(ShowCapabilityMismatch));
        OnPropertyChanged(nameof(ShowInputGroupBanner));
    }
}
