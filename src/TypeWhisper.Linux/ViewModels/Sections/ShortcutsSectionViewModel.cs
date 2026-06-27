using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class ShortcutsSectionViewModel : ObservableObject
{
    private const string DictationShortcutId = DictationShortcutSpecFactory.DictationShortcutId;

    private readonly HotkeyService _hotkey;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<IDeShortcutWriter> _writers;

    // Cached lazily: IsCurrentDesktop hits the filesystem (BinaryExists) so
    // we don't want to rerun it for every UI-bound property. The resolved writer
    // lives in the ActiveWriter property's backing `field`; this flag marks it computed.
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

    // Whether the compositor-bind fallback section is expanded. Starts open only
    // when those binds are the user's actual route to a global hotkey (see
    // ComputeCompositorBindsRelevant); otherwise it's a collapsed alternative so
    // the evdev backend reads as the headline path.
    [ObservableProperty]
    private bool _compositorBindsExpanded;

    // Test-friendly overload — production wiring passes the DI-registered
    // writer collection through the three-arg constructor.
    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
        : this(hotkey, settings, [])
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
        _compositorBindsExpanded = ComputeCompositorBindsRelevant();
    }

    // ReSharper disable once UnusedMember.Global  public ViewModel property (recording-mode options for selection UI); not currently bound in-tree
    public IReadOnlyList<RecordingMode> Modes { get; } =
        [RecordingMode.Toggle, RecordingMode.PushToTalk, RecordingMode.Hybrid];

    public string ActiveBackendId => _hotkey.ActiveBackendId ?? "(not initialized)";

    public string ActiveBackendDisplayName =>
        _hotkey.ActiveBackendDisplayName ?? "(not initialized)";

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string SessionType =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";

    public bool SupportsPressRelease => _hotkey.ActiveBackendSupportsPressRelease ?? false;

    public string SupportsPressReleaseText =>
        Loc.Instance[SupportsPressRelease ? "Common.Yes" : "Common.No"];

    // SharpHook on Wayland delivers press+release but only while TypeWhisper has focus,
    // so SupportsPressRelease ≠ global scope — labelling it "Global" would mislead users.
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
                ? Loc.Instance["Shortcuts.ScopeGlobal"]
                : Loc.Instance["Shortcuts.ScopeFocusedOnly"];
        }
    }

    public bool ShowCapabilityMismatch =>
        _hotkey.BackendRequiresToggleMode && Mode != RecordingMode.Toggle;

    // Show the banner only on Wayland when we genuinely can't open a keyboard
    // node. Gating on actual access (not input-group membership) is correct now
    // that the uaccess rule grants access via a session ACL without the group —
    // a membership check would nag users who already have working access.
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public bool ShowKeyboardAccessBanner
    {
        get
        {
            if (!IsWaylandSession())
            {
                return false;
            }

            return !InputDeviceAccessCheck.HasKeyboardAccess();
        }
    }

    // The exact command the keyboard-access setup runs, offered as a copyable
    // fallback for users who'd rather not click through the one-prompt installer.
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string KeyboardAccessCommand => InputAccessSetupHelper.ManualInstallCommand();

    // Bare binary name relies on the Phase 4 single-instance IPC: a second
    // invocation toggles the existing instance instead of launching a new one.
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string CustomShortcutCommand => "typewhisper";

    public string CompositorBindsToggleLabel =>
        Loc.Instance[
            CompositorBindsExpanded
                ? "Shortcuts.CompositorBindsHide"
                : "Shortcuts.CompositorBindsShow"
        ];

    // Compositor binds are the user's actual path to a global hotkey only when
    // the in-process backend isn't already carrying it: on Wayland with evdev
    // turned off, or with no keyboard access yet. Otherwise (evdev active, or
    // X11 where the in-app hook is already global) they're a lesser alternative —
    // press-only on most desktops and limited to the main dictation toggle — so
    // the section starts collapsed.
    private bool ComputeCompositorBindsRelevant()
    {
        if (!IsWaylandSession())
        {
            return false;
        }

        return !WaylandEvdevHotkeysEnabled || !InputDeviceAccessCheck.HasKeyboardAccess();
    }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public bool ShowPushToTalkSnippet => DesktopName is "Hyprland" or "Sway";

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
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

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string PushToTalkReleaseSnippet =>
        DesktopName switch
        {
            "Hyprland" => "bindr = CTRL SHIFT, SPACE, exec, typewhisper record stop",
            "Sway" => "bindsym --release $mod+space exec typewhisper record stop",
            _ => ""
        };

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string PushToTalkSnippetHint =>
        DesktopName switch
        {
            "Hyprland" => Loc.Instance["Shortcuts.PushToTalkSnippetHintHyprland"],
            "Sway" => Loc.Instance["Shortcuts.PushToTalkSnippetHintSway"],
            _ => ""
        };

    // DesktopDetector normalizes edge cases like "ubuntu:GNOME".
    // "KDE Plasma" → "KDE" keeps legacy snippet-display keys intact.
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string DesktopName
    {
        get
        {
            var name = DesktopDetector.DisplayName();
            return name == "KDE Plasma" ? "KDE" : name;
        }
    }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string DesktopInstructions =>
        DesktopName switch
        {
            "GNOME" => Loc.Instance["Shortcuts.DesktopInstructionsGnome"],
            "KDE" => Loc.Instance["Shortcuts.DesktopInstructionsKde"],
            "Hyprland" => Loc.Instance["Shortcuts.DesktopInstructionsHyprland"],
            "Sway" => Loc.Instance["Shortcuts.DesktopInstructionsSway"],
            "XFCE" => Loc.Instance["Shortcuts.DesktopInstructionsXfce"],
            "Cinnamon" => Loc.Instance["Shortcuts.DesktopInstructionsCinnamon"],
            "MATE" => Loc.Instance["Shortcuts.DesktopInstructionsMate"],
            _ => Loc.Instance["Shortcuts.DesktopInstructionsGeneric"]
        };

    private IDeShortcutWriter? ActiveWriter
    {
        get
        {
            if (_activeWriterCached)
            {
                return field;
            }

            _activeWriterCached = true;
            foreach (var w in _writers)
            {
                try
                {
                    if (!w.IsCurrentDesktop())
                    {
                        continue;
                    }

                    field = w;
                    break;
                }
                catch
                {
                    // A buggy writer must never break the Shortcuts panel.
                }
            }

            return field;
        }
    }

    public bool CanSetupAutomatically => ActiveWriter is not null;

    public string SetupAutomaticallyLabel =>
        ActiveWriter is null
            ? Loc.Instance["TextInsertion.SetUpAutomatically"]
            : Loc.Instance.GetString("Shortcuts.SetupAutomaticallyOn", ActiveWriter.DisplayName);

    public string IntegrationPreview
    {
        get
        {
            var w = ActiveWriter;
            return w is null ? string.Empty : w.PreviewLines(BuildSpec(w));
        }
    }

    // Uses the shared factory so this panel and the onboarding checklist register
    // identical shortcuts — otherwise one could install a bind the other wouldn't recognize.
    private DeShortcutSpec BuildSpec(IDeShortcutWriter writer)
    {
        return DictationShortcutSpecFactory.Build(_settings, writer);
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
                _settings.Current with { RecentTranscriptionsHotkey = _hotkey.CurrentRecentTranscriptionsHotkeyString }
            );
            StatusMessage = string.IsNullOrWhiteSpace(
                _hotkey.CurrentRecentTranscriptionsHotkeyString
            )
                ? Loc.Instance["Shortcuts.RecentTranscriptionsHotkeyCleared"]
                : Loc.Instance.GetString(
                    "Shortcuts.RecentTranscriptionsHotkeySet",
                    _hotkey.CurrentRecentTranscriptionsHotkeyString
                );
            RecentTranscriptionsHotkeyText = _hotkey.CurrentRecentTranscriptionsHotkeyString;
        }
        else
        {
            StatusMessage =
                Loc.Instance.GetString("Shortcuts.HotkeyParseOrCollide", RecentTranscriptionsHotkeyText);
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
                ? Loc.Instance["Shortcuts.CopyLastTranscriptionHotkeyCleared"]
                : Loc.Instance.GetString(
                    "Shortcuts.CopyLastTranscriptionHotkeySet",
                    _hotkey.CurrentCopyLastTranscriptionHotkeyString
                );
            CopyLastTranscriptionHotkeyText = _hotkey.CurrentCopyLastTranscriptionHotkeyString;
        }
        else
        {
            StatusMessage =
                Loc.Instance.GetString("Shortcuts.HotkeyParseOrCollide", CopyLastTranscriptionHotkeyText);
        }
    }

    [RelayCommand]
    private void ApplyTransformSelectionHotkey()
    {
        if (_hotkey.TrySetTransformSelectionHotkeyFromString(TransformSelectionHotkeyText))
        {
            _settings.Save(
                _settings.Current with { TransformSelectionHotkey = _hotkey.CurrentTransformSelectionHotkeyString }
            );
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentTransformSelectionHotkeyString)
                ? Loc.Instance["Shortcuts.TransformSelectionHotkeyCleared"]
                : Loc.Instance.GetString(
                    "Shortcuts.TransformSelectionHotkeySet",
                    _hotkey.CurrentTransformSelectionHotkeyString
                );
            TransformSelectionHotkeyText = _hotkey.CurrentTransformSelectionHotkeyString;
        }
        else
        {
            StatusMessage =
                Loc.Instance.GetString("Shortcuts.HotkeyParseOrCollide", TransformSelectionHotkeyText);
        }
    }

    [RelayCommand]
    private async Task SetupAutomaticallyAsync()
    {
        var writer = ActiveWriter;
        if (writer is null)
        {
            IntegrationStatusMessage = Loc.Instance["Shortcuts.NoAutomaticSetup"];
            return;
        }

        IntegrationStatusMessage =
            Loc.Instance.GetString("Shortcuts.InstallingShortcut", writer.DisplayName);
        try
        {
            var result = await writer
                .WriteAsync(BuildSpec(writer), CancellationToken.None)
                .ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = Loc.Instance.GetString("Shortcuts.SetupFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RemoveIntegrationAsync()
    {
        var writer = ActiveWriter;
        if (writer is null)
        {
            IntegrationStatusMessage = Loc.Instance["Shortcuts.NoAutomaticSetup"];
            return;
        }

        IntegrationStatusMessage =
            Loc.Instance.GetString("Shortcuts.RemovingShortcut", writer.DisplayName);
        try
        {
            var result = await writer
                .RemoveAsync(DictationShortcutId, CancellationToken.None)
                .ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = Loc.Instance.GetString("Shortcuts.RemovalFailed", ex.Message);
        }
    }

    private static string FormatResultMessage(DeShortcutWriteResult result)
    {
        var prefix = result.Success ? "" : Loc.Instance["Shortcuts.CouldNotFinishPrefix"];
        var msg = string.IsNullOrWhiteSpace(result.UserMessage)
            ? result.Success ? Loc.Instance["Shortcuts.Done"] : Loc.Instance["Shortcuts.UnknownError"]
            : result.UserMessage;
        return string.IsNullOrWhiteSpace(result.Warning)
            ? prefix + msg
            : $"{prefix}{msg} ({result.Warning})";
    }

    [RelayCommand]
    private void CopyCustomShortcut()
    {
        CopyCustomShortcutRequested?.Invoke(this, CustomShortcutCommand);
        StatusMessage = Loc.Instance.GetString("Shortcuts.CopiedToClipboard", CustomShortcutCommand);
    }

    [RelayCommand]
    private void ToggleCompositorBinds()
    {
        CompositorBindsExpanded = !CompositorBindsExpanded;
    }

    partial void OnCompositorBindsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(CompositorBindsToggleLabel));
    }

    [RelayCommand]
    private void CopyPushToTalkPressSnippet()
    {
        if (string.IsNullOrEmpty(PushToTalkPressSnippet))
        {
            return;
        }

        CopyCustomShortcutRequested?.Invoke(this, PushToTalkPressSnippet);
        StatusMessage = Loc.Instance["Shortcuts.CopiedPressBind"];
    }

    [RelayCommand]
    private void CopyPushToTalkReleaseSnippet()
    {
        if (string.IsNullOrEmpty(PushToTalkReleaseSnippet))
        {
            return;
        }

        CopyCustomShortcutRequested?.Invoke(this, PushToTalkReleaseSnippet);
        StatusMessage = Loc.Instance["Shortcuts.CopiedReleaseBind"];
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
        StatusMessage = Loc.Instance["Shortcuts.CopiedPushToTalkBinds"];
    }

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (_hotkey.TrySetHotkeyFromString(HotkeyText))
        {
            _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            StatusMessage = Loc.Instance.GetString("Shortcuts.HotkeySet", _hotkey.CurrentHotkeyString);
            HotkeyText = _hotkey.CurrentHotkeyString;
        }
        else
        {
            StatusMessage =
                Loc.Instance.GetString("Shortcuts.HotkeyParseFailed", HotkeyText);
        }
    }

    [RelayCommand]
    private void ApplyPromptPaletteHotkey()
    {
        if (_hotkey.TrySetPromptPaletteHotkeyFromString(PromptPaletteHotkeyText))
        {
            _settings.Save(
                _settings.Current with { PromptPaletteHotkey = _hotkey.CurrentPromptPaletteHotkeyString }
            );
            StatusMessage = string.IsNullOrWhiteSpace(_hotkey.CurrentPromptPaletteHotkeyString)
                ? Loc.Instance["Shortcuts.PromptPaletteHotkeyCleared"]
                : Loc.Instance.GetString(
                    "Shortcuts.PromptPaletteHotkeySet",
                    _hotkey.CurrentPromptPaletteHotkeyString
                );
            PromptPaletteHotkeyText = _hotkey.CurrentPromptPaletteHotkeyString;
        }
        else
        {
            StatusMessage =
                Loc.Instance.GetString("Shortcuts.PromptPaletteHotkeyParseFailed", PromptPaletteHotkeyText);
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
            RecordingMode.Toggle => Loc.Instance["Shortcuts.ModeToggleStatus"],
            RecordingMode.PushToTalk => Loc.Instance["Shortcuts.ModePushToTalkStatus"],
            RecordingMode.Hybrid => Loc.Instance["Shortcuts.ModeHybridStatus"],
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
            ? Loc.Instance["Shortcuts.GlobalReadsEnabled"]
            : Loc.Instance["Shortcuts.FocusedOnlyFallback"];

        // Surface the compositor-bind fallback the moment evdev stops carrying the
        // hotkey (and re-collapse it once evdev is back), matching the rule that it
        // only auto-expands when it's the user's real route to a global hotkey.
        CompositorBindsExpanded = ComputeCompositorBindsRelevant();

        // Hot-swap immediately: a restart-only opt-out would be a consent gap
        // for a setting that controls global keyboard event access.
        var task = SwitchBackendAndNotifyAsync();
        task.ContinueWith(
            t =>
                StatusMessage = Loc.Instance.GetString(
                    "Shortcuts.BackendSwitchFailed",
                    t.Exception?.GetBaseException().Message ?? ""
                ),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.FromCurrentSynchronizationContext()
        );
    }

    private async Task SwitchBackendAndNotifyAsync()
    {
        try
        {
            // No ConfigureAwait(false): OnPropertyChanged must fire on the UI thread.
            await _hotkey.SwitchBackendAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Instance.GetString("Shortcuts.BackendSwitchFailed", ex.Message);
            return;
        }

        OnPropertyChanged(nameof(ActiveBackendId));
        OnPropertyChanged(nameof(ActiveBackendDisplayName));
        OnPropertyChanged(nameof(SupportsPressRelease));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(ShowCapabilityMismatch));
        OnPropertyChanged(nameof(ShowKeyboardAccessBanner));
    }
}