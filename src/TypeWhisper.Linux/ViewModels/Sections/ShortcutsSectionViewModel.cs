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

    // Cached lazily: IsCurrentDesktop hits the filesystem (BinaryExists) so
    // we don't want to rerun it for every UI-bound property.
    private IDeShortcutWriter? _activeWriterCache;

    private bool _activeWriterCached;

    [ObservableProperty]
    private string _copyLastTranscriptionHotkeyText = "";

    [ObservableProperty]
    private string _hotkeyText = "";

    [ObservableProperty]
    private string _integrationStatusMessage = "";

    [ObservableProperty]
    private RecordingMode _mode;

    [ObservableProperty]
    private string _promptPaletteHotkeyText = "";

    [ObservableProperty]
    private string _recentTranscriptionsHotkeyText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _transformSelectionHotkeyText = "";

    [ObservableProperty]
    private bool _waylandEvdevHotkeysEnabled;

    // Test-friendly overload — production wiring passes the DI-registered
    // writer collection through the three-arg constructor.
    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
        : this(hotkey, settings, Array.Empty<IDeShortcutWriter>())
    {
    }

    public ShortcutsSectionViewModel(
        HotkeyService hotkey,
        ISettingsService settings,
        IEnumerable<IDeShortcutWriter> writers
    )
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

    public IReadOnlyList<RecordingMode> Modes { get; } =
        [RecordingMode.Toggle, RecordingMode.PushToTalk, RecordingMode.Hybrid];

    public string ActiveBackendId => _hotkey.ActiveBackendId ?? "(not initialized)";

    public string ActiveBackendDisplayName =>
        _hotkey.ActiveBackendDisplayName ?? "(not initialized)";

    public string SessionType =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";

    public bool SupportsPressRelease => _hotkey.ActiveBackendSupportsPressRelease ?? false;

    // Distinct from SupportsPressRelease: SharpHook on Wayland delivers
    // press+release but only while TypeWhisper has focus. Labelling that
    // "Global" would mislead users diagnosing a broken hotkey.
    public string ScopeText
    {
        get
        {
            var global = _hotkey.ActiveBackendIsGlobalScope;
            if (global is null)
            {
                return "(not initialized)";
            }

            return global.Value
                ? "Global (works in any focused window)"
                : "Focused only (TypeWhisper window)";
        }
    }

    public bool ShowCapabilityMismatch =>
        _hotkey.BackendRequiresToggleMode && Mode != RecordingMode.Toggle;

    // Hide the banner when the group check returns null (e.g. /proc
    // unavailable) so we don't nag users we can't actually advise.
    public bool ShowInputGroupBanner
    {
        get
        {
            if (!IsWaylandSession())
            {
                return false;
            }

            var inGroup = InputGroupCheck.CurrentUserInInputGroup();
            return inGroup == false;
        }
    }

    public string InputGroupCommand => AddToInputGroupCommand;

    // Bare binary name relies on the Phase 4 single-instance IPC: a second
    // invocation toggles the existing instance instead of launching a new one.
    public string CustomShortcutCommand => "typewhisper";

    public bool ShowPushToTalkSnippet => DesktopName is "Hyprland" or "Sway";

    public string PushToTalkPressSnippet =>
        DesktopName switch
        {
            "Hyprland" => "bind  = CTRL SHIFT, SPACE, exec, typewhisper record start",
            // `--no-repeat` keeps a held key from hammering record.start many
            // times per second. The orchestrator is idempotent so it's safe,
            // just noisy.
            "Sway" => "bindsym --no-repeat $mod+space exec typewhisper record start",
            _ => ""
        };

    public string PushToTalkReleaseSnippet =>
        DesktopName switch
        {
            "Hyprland" => "bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop",
            "Sway" => "bindsym --release $mod+space exec typewhisper record stop",
            _ => ""
        };

    public string PushToTalkSnippetHint =>
        DesktopName switch
        {
            "Hyprland" =>
                "Hyprland supports separate press/release binds. Use this pair for true push-to-talk:",
            "Sway" =>
                "Sway supports a press/release pair. Use these two binds for true push-to-talk:",
            _ => ""
        };

    // Route through DesktopDetector so this VM and the writer-selection
    // logic agree on edge cases like "ubuntu:GNOME". "KDE Plasma" → "KDE"
    // keeps the legacy snippet-display keys intact.
    public string DesktopName
    {
        get
        {
            var name = DesktopDetector.DisplayName();
            return name == "KDE Plasma" ? "KDE" : name;
        }
    }

    public string DesktopInstructions =>
        DesktopName switch
        {
            "GNOME" =>
                "Open Settings → Keyboard → View and Customize Shortcuts → Custom Shortcuts.\n"
                + "Add a new entry, paste the command above, and pick the keys you want.",
            "KDE" => "Open System Settings → Shortcuts → Custom Shortcuts.\n"
                     + "Edit → New → Global Shortcut → Command/URL, paste the command above, and assign a trigger.",
            "Hyprland" => "Edit ~/.config/hypr/hyprland.conf and add a bind line, e.g.:\n"
                          + "  bind = SUPER, SPACE, exec, typewhisper\n"
                          + "Reload with `hyprctl reload`.",
            "Sway" => "Edit ~/.config/sway/config and add a bindsym, e.g.:\n"
                      + "  bindsym $mod+space exec typewhisper\n"
                      + "Reload with `swaymsg reload`.",
            "XFCE" => "Open Settings → Keyboard → Application Shortcuts → Add.\n"
                      + "Paste the command above and choose the key combination when prompted.",
            "Cinnamon" => "Open System Settings → Keyboard → Shortcuts → Custom Shortcuts.\n"
                          + "Add a custom shortcut with the command above and bind a key.",
            "MATE" => "Open System Settings → Keyboard Shortcuts → Add.\n"
                      + "Paste the command above and assign a key combination.",
            _ =>
                "Open your desktop's keyboard settings and add a custom shortcut that runs the command above.\n"
                + "Bind it to any key combination you like (e.g. Ctrl+Shift+Space)."
        };

    private IDeShortcutWriter? ActiveWriter
    {
        get
        {
            if (_activeWriterCached)
            {
                return _activeWriterCache;
            }

            _activeWriterCached = true;
            foreach (var w in _writers)
            {
                try
                {
                    if (w.IsCurrentDesktop())
                    {
                        _activeWriterCache = w;
                        break;
                    }
                }
                catch
                {
                    // A buggy writer must never break the Shortcuts panel.
                }
            }

            return _activeWriterCache;
        }
    }

    public bool CanSetupAutomatically => ActiveWriter is not null;

    public string SetupAutomaticallyLabel =>
        ActiveWriter is null
            ? "Set up automatically"
            : $"Set up automatically ({ActiveWriter.DisplayName})";

    public string IntegrationPreview
    {
        get
        {
            var w = ActiveWriter;
            if (w is null)
            {
                return string.Empty;
            }

            return w.PreviewLines(BuildSpec(w));
        }
    }

    // For PTT-capable DEs emit the record start/stop/cancel triplet so the
    // installed bind drives the Phase 5 CLI directly. Trigger defaults from
    // the user's configured toggle hotkey so they don't re-enter it here.
    private DeShortcutSpec BuildSpec(IDeShortcutWriter writer)
    {
        var trigger = string.IsNullOrWhiteSpace(_settings.Current.ToggleHotkey)
            ? "Ctrl+Shift+Space"
            : _settings.Current.ToggleHotkey;
        var gui = ResolveGuiCommand();
        if (writer.SupportsPushToTalk)
        {
            return new DeShortcutSpec(
                DictationShortcutId,
                DictationDisplayName,
                trigger,
                $"{gui} record start",
                $"{gui} record stop",
                // Cancel mirrors the trigger but swaps Space → Escape.
                // It only fires when the user has configured a cancel
                // accelerator; we synthesize a reasonable default for
                // them rather than asking up-front.
                SwapKeyForCancel(trigger),
                $"{gui} record cancel"
            );
        }

        return new DeShortcutSpec(
            DictationShortcutId,
            DictationDisplayName,
            trigger,
            gui,
            null,
            null,
            null
        );
    }

    /// <summary>
    ///     The command the auto-installed shortcut should invoke. We resolve
    ///     to the GUI's own apphost path rather than bare <c>typewhisper</c>
    ///     because <c>CliInstallService</c> installs the separate
    ///     <c>TypeWhisper.Cli</c> executable under the same name; when that
    ///     CLI shadows the GUI on PATH the bare command resolves to a binary
    ///     that doesn't implement <c>record start/stop/cancel</c> and the
    ///     shortcut fails with "unknown record verb" instead of toggling
    ///     dictation. We only trust <see cref="Environment.ProcessPath" />
    ///     when it actually points at the apphost — a <c>dotnet run</c> /
    ///     IDE launch reports the dotnet host instead, and emitting
    ///     <c>/usr/bin/dotnet record start</c> would also fail. In that
    ///     source-run case we fall back to the bare <c>typewhisper</c> name,
    ///     which is fine because dev runs don't usually install the CLI
    ///     side-by-side with the GUI.
    /// </summary>
    private static string ResolveGuiCommand()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)
            && string.Equals(Path.GetFileName(path), "typewhisper", StringComparison.Ordinal))
        {
            return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
        }

        return "typewhisper";
    }

    private static string SwapKeyForCancel(string trigger)
    {
        // Replace just the terminal key — the modifier stack stays
        // identical so the cancel binding can't accidentally collide
        // with the start binding by virtue of being too similar.
        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return "Ctrl+Shift+Escape";
        }

        parts[^1] = "Escape";
        return string.Join('+', parts);
    }

    // VMs don't have direct clipboard access in Avalonia — the view
    // subscribes and writes via TopLevel.Clipboard.
    public event EventHandler<string>? CopyCustomShortcutRequested;

    private static bool IsWaylandSession()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase
        );
    }

    [RelayCommand]
    private void ApplyRecentTranscriptionsHotkey()
    {
        if (_hotkey.TrySetRecentTranscriptionsHotkeyFromString(RecentTranscriptionsHotkeyText))
        {
            _settings.Save(
                _settings.Current with
                {
                    RecentTranscriptionsHotkey = _hotkey.CurrentRecentTranscriptionsHotkeyString
                }
            );
            StatusMessage = string.IsNullOrWhiteSpace(
                _hotkey.CurrentRecentTranscriptionsHotkeyString
            )
                ? "Recent transcriptions hotkey cleared."
                : $"Recent transcriptions hotkey set to {_hotkey.CurrentRecentTranscriptionsHotkeyString}.";
            RecentTranscriptionsHotkeyText = _hotkey.CurrentRecentTranscriptionsHotkeyString;
        }
        else
        {
            StatusMessage =
                $"Could not parse '{RecentTranscriptionsHotkeyText}' or it collides with another shortcut.";
        }
    }

    [RelayCommand]
    private void ApplyCopyLastTranscriptionHotkey()
    {
        if (_hotkey.TrySetCopyLastTranscriptionHotkeyFromString(CopyLastTranscriptionHotkeyText))
        {
            _settings.Save(
                _settings.Current with
                {
                    CopyLastTranscriptionHotkey = _hotkey.CurrentCopyLastTranscriptionHotkeyString
                }
            );
            StatusMessage = string.IsNullOrWhiteSpace(
                _hotkey.CurrentCopyLastTranscriptionHotkeyString
            )
                ? "Copy last transcription hotkey cleared."
                : $"Copy last transcription hotkey set to {_hotkey.CurrentCopyLastTranscriptionHotkeyString}.";
            CopyLastTranscriptionHotkeyText = _hotkey.CurrentCopyLastTranscriptionHotkeyString;
        }
        else
        {
            StatusMessage =
                $"Could not parse '{CopyLastTranscriptionHotkeyText}' or it collides with another shortcut.";
        }
    }

    [RelayCommand]
    private void ApplyTransformSelectionHotkey()
    {
        if (_hotkey.TrySetTransformSelectionHotkeyFromString(TransformSelectionHotkeyText))
        {
            _settings.Save(
                _settings.Current with
                {
                    TransformSelectionHotkey = _hotkey.CurrentTransformSelectionHotkeyString
                }
            );
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentTransformSelectionHotkeyString)
                ? "Transform selection hotkey cleared."
                : $"Transform selection hotkey set to {_hotkey.CurrentTransformSelectionHotkeyString}.";
            TransformSelectionHotkeyText = _hotkey.CurrentTransformSelectionHotkeyString;
        }
        else
        {
            StatusMessage =
                $"Could not parse '{TransformSelectionHotkeyText}' or it collides with another shortcut.";
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
            var result = await writer
                .WriteAsync(BuildSpec(writer), CancellationToken.None)
                .ConfigureAwait(true);
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
            var result = await writer
                .RemoveAsync(DictationShortcutId, CancellationToken.None)
                .ConfigureAwait(true);
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
        var msg = string.IsNullOrWhiteSpace(result.UserMessage)
            ? result.Success ? "Done." : "Unknown error."
            : result.UserMessage;
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
        if (string.IsNullOrEmpty(PushToTalkPressSnippet))
        {
            return;
        }

        CopyCustomShortcutRequested?.Invoke(this, PushToTalkPressSnippet);
        StatusMessage = "Copied press bind to the clipboard.";
    }

    [RelayCommand]
    private void CopyPushToTalkReleaseSnippet()
    {
        if (string.IsNullOrEmpty(PushToTalkReleaseSnippet))
        {
            return;
        }

        CopyCustomShortcutRequested?.Invoke(this, PushToTalkReleaseSnippet);
        StatusMessage = "Copied release bind to the clipboard.";
    }

    [RelayCommand]
    private void CopyPushToTalkPair()
    {
        if (!ShowPushToTalkSnippet)
        {
            return;
        }

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
            StatusMessage =
                $"Could not parse '{HotkeyText}'. Try e.g. Ctrl+Shift+Space, Alt+F9, Ctrl+K.";
        }
    }

    [RelayCommand]
    private void ApplyPromptPaletteHotkey()
    {
        if (_hotkey.TrySetPromptPaletteHotkeyFromString(PromptPaletteHotkeyText))
        {
            _settings.Save(
                _settings.Current with
                {
                    PromptPaletteHotkey = _hotkey.CurrentPromptPaletteHotkeyString
                }
            );
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentPromptPaletteHotkeyString)
                ? "Prompt palette hotkey cleared."
                : $"Prompt palette hotkey set to {_hotkey.CurrentPromptPaletteHotkeyString}.";
            PromptPaletteHotkeyText = _hotkey.CurrentPromptPaletteHotkeyString;
        }
        else
        {
            StatusMessage =
                $"Could not parse '{PromptPaletteHotkeyText}'. Try e.g. Ctrl+Shift+P, Alt+F10, Ctrl+K.";
        }
    }

    partial void OnModeChanged(RecordingMode value)
    {
        if (_settings.Current.Mode == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { Mode = value });
        StatusMessage = value switch
        {
            RecordingMode.Toggle => "Press the hotkey to start, press again to stop.",
            RecordingMode.PushToTalk =>
                "Hold the hotkey to record; release to stop and transcribe.",
            RecordingMode.Hybrid =>
                "Starts immediately. Short press keeps recording; hold past ~600 ms stops on release.",
            _ => ""
        };
        OnPropertyChanged(nameof(ShowCapabilityMismatch));
    }

    partial void OnWaylandEvdevHotkeysEnabledChanged(bool value)
    {
        if (_settings.Current.WaylandEvdevHotkeysEnabled == value)
        {
            return;
        }

        _settings.Save(_settings.Current with { WaylandEvdevHotkeysEnabled = value });
        StatusMessage = value
            ? "Global keyboard reads enabled."
            : "Falling back to focused-only hotkeys.";

        // Hot-swap immediately so disabling actually stops the evdev
        // reader — a delayed (restart-only) opt-out is a real consent gap
        // for a setting that controls global keyboard event access.
        var task = SwitchBackendAndNotifyAsync();
        task.ContinueWith(
            t =>
                StatusMessage = $"Backend switch failed: {t.Exception?.GetBaseException().Message}",
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext()
        );
    }

    private async Task SwitchBackendAndNotifyAsync()
    {
        try
        {
            // Keep the captured UI sync context — the OnPropertyChanged calls
            // below fire PropertyChanged on whatever thread we resume on, and
            // Avalonia bindings require those events on the UI thread.
            await _hotkey.SwitchBackendAsync();
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