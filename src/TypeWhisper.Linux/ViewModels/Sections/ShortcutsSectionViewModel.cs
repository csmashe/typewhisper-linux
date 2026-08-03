using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

internal enum ManagedDesktopIntegrationState
{
    Unknown,
    Absent,
    Current,
    Stale
}

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

    // Cached keyboard-access probe result, populated off the UI thread by
    // RefreshKeyboardAccessAsync. InputDeviceAccessCheck.HasKeyboardAccess() opens
    // every /dev/input keyboard node and ioctls it — ~0.5s on a typical desktop — so
    // it must never run during view render. null = not yet probed (treated as "has
    // access" so the no-access banner/fallback don't flash before the probe returns).
    private bool? _hasKeyboardAccess;

    // Monotonic token so overlapping probes (the section can be reshown while a
    // prior probe is still running) only let the newest one write back; an older
    // probe that finishes late must not clobber the latest result.
    private int _keyboardAccessRefreshVersion;

    // Desktop-integration probes are independent from M5's startup ownership probe: config
    // presence can identify a stale managed entry, but does not prove that the desktop route is
    // live. The generation prevents an old spec probe from overwriting a later setting change or
    // an explicit refresh/removal result.
    private int _desktopIntegrationRefreshVersion;
    private ManagedDesktopIntegrationState _desktopIntegrationState =
        ManagedDesktopIntegrationState.Unknown;
    private Task _pendingDesktopIntegrationRefresh = Task.CompletedTask;

    // While false, the compositor-bind fallback auto-tracks keyboard access: every
    // probe re-applies ComputeCompositorBindsRelevant() so the disclosure stays in
    // sync as access changes (e.g. granted by onboarding). An explicit Show/Hide
    // toggle sets this true so the user's choice is no longer overridden; toggling
    // the evdev setting re-arms auto-tracking (a deliberate disclosure reset).
    private bool _compositorBindsUserControlled;

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

        // Keyboard access is probed on section activation (RefreshKeyboardAccess),
        // not here: the probe is expensive (see RefreshKeyboardAccessAsync) and this
        // VM is built eagerly at startup, before first-run onboarding can grant
        // access — probing in the ctor would both stall startup and cache a stale
        // pre-onboarding result.
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

            // Cached probe (RefreshKeyboardAccessAsync) — show the banner only once
            // we've confirmed there's no access; stay hidden while the probe is pending.
            return _hasKeyboardAccess == false;
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

        // evdev opted out → compositor binds are the route, no access probe needed.
        if (!WaylandEvdevHotkeysEnabled)
        {
            return true;
        }

        // evdev on → relevant only once the cached probe confirms no keyboard access.
        // While pending (null) stay collapsed; RefreshKeyboardAccessAsync re-runs this.
        return _hasKeyboardAccess == false;
    }

    // Invoked via RefreshSectionState each time the Shortcuts section is shown. Re-probes so the
    // banner/fallback reflect access granted since construction — e.g. by first-run
    // onboarding, which grants access via HotkeyService outside this VM. Fire-and-forget:
    // the probe updates the bound properties on completion.
    private void RefreshKeyboardAccess()
    {
        _ = RefreshKeyboardAccessAsync();
    }

    // Both checks are read-only; desktop settings change only via the explicit
    // setup/remove commands below.
    public void RefreshSectionState()
    {
        RefreshKeyboardAccess();
        _ = ScheduleDesktopIntegrationRefresh();
    }

    // Probe keyboard access off the UI thread, then refresh the access-dependent
    // properties. InputDeviceAccessCheck.HasKeyboardAccess() opens every /dev/input
    // keyboard node (~0.5s) — running it during the constructor or a binding getter
    // froze the Shortcuts tab on open. No ConfigureAwait(false): the continuation
    // touches bound properties and must resume on the UI thread.
    private async Task RefreshKeyboardAccessAsync()
    {
        if (!IsWaylandSession())
        {
            return;
        }

        // Claim this probe's version before awaiting (runs on the UI thread, so the
        // increment and the post-await check below never interleave with each other).
        var version = ++_keyboardAccessRefreshVersion;
        var hasAccess = await Task.Run(InputDeviceAccessCheck.HasKeyboardAccess);

        // A newer probe superseded us while we awaited — drop this stale result.
        if (version != _keyboardAccessRefreshVersion)
        {
            return;
        }

        _hasKeyboardAccess = hasAccess;
        OnPropertyChanged(nameof(ShowKeyboardAccessBanner));

        // Keep the fallback's auto-expand in sync with access until the user takes
        // control with an explicit Show/Hide.
        if (!_compositorBindsUserControlled)
        {
            CompositorBindsExpanded = ComputeCompositorBindsRelevant();
        }
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

    public bool CanWriteDesktopIntegration
    {
        get
        {
            var writer = ActiveWriter;
            return writer is not null && BuildSpec(writer) is not null;
        }
    }

    public bool ShowStaleIntegrationBanner =>
        _desktopIntegrationState == ManagedDesktopIntegrationState.Stale;

    public bool CanRefreshDesktopIntegration =>
        ShowStaleIntegrationBanner && CanWriteDesktopIntegration;

    public bool CanRemoveDesktopIntegration =>
        _desktopIntegrationState is ManagedDesktopIntegrationState.Current
            or ManagedDesktopIntegrationState.Stale;

    public string StaleIntegrationMessage
    {
        get
        {
            var writer = ActiveWriter;
            if (!ShowStaleIntegrationBanner || writer is null)
            {
                return string.Empty;
            }

            return BuildSpec(writer) is null
                ? Loc.Instance.GetString(
                    "Shortcuts.DesktopIntegrationStaleUnsupported",
                    writer.DisplayName,
                    GetModeDisplayName()
                )
                : Loc.Instance["Shortcuts.DesktopIntegrationStaleHint"];
        }
    }

    // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- backing field is mutated by the deliberate versioned-probe race guard / invalidate-around-mutation pattern; keep it a field.
    internal ManagedDesktopIntegrationState DesktopIntegrationState =>
        _desktopIntegrationState;

    // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- backing field is reassigned by ScheduleDesktopIntegrationRefresh under the deliberate race-guard pattern; keep it a field.
    internal Task PendingDesktopIntegrationRefresh => _pendingDesktopIntegrationRefresh;

    public string SetupAutomaticallyLabel =>
        ActiveWriter is null
            ? Loc.Instance["TextInsertion.SetUpAutomatically"]
            : Loc.Instance.GetString(
                ShowStaleIntegrationBanner
                    ? "Shortcuts.RefreshDesktopIntegrationOn"
                    : "Shortcuts.SetupAutomaticallyOn",
                ActiveWriter.DisplayName
            );

    public string IntegrationPreview
    {
        get
        {
            var w = ActiveWriter;
            if (w is null)
            {
                return string.Empty;
            }

            var spec = BuildSpec(w);
            return spec is null ? GetUnsupportedModeMessage(w) : w.PreviewLines(spec);
        }
    }

    // Uses the shared factory so this panel and the onboarding checklist register
    // identical shortcuts — otherwise one could install a bind the other wouldn't recognize.
    private DeShortcutSpec? BuildSpec(IDeShortcutWriter writer)
    {
        return DictationShortcutSpecFactory.Build(_settings, writer);
    }

    internal async Task RefreshDesktopIntegrationStateAsync(CancellationToken ct)
    {
        var version = Interlocked.Increment(ref _desktopIntegrationRefreshVersion);
        try
        {
            // Capture both before the first await. Later setting changes start a newer version,
            // so this result cannot describe a different hotkey/mode by accident.
            var writer = ActiveWriter;
            var spec = writer is null ? null : BuildSpec(writer);
            if (writer is null)
            {
                SetDesktopIntegrationStateIfCurrent(
                    version,
                    ManagedDesktopIntegrationState.Absent
                );
                return;
            }

            if (spec is not null)
            {
                var exact = await writer.IsInstalledAsync(spec, ct).ConfigureAwait(true);
                if (exact)
                {
                    SetDesktopIntegrationStateIfCurrent(
                        version,
                        ManagedDesktopIntegrationState.Current
                    );
                    return;
                }
            }

            var present = await writer
                .IsManagedShortcutPresentAsync(DictationShortcutId, ct)
                .ConfigureAwait(true);
            SetDesktopIntegrationStateIfCurrent(
                version,
                present
                    ? ManagedDesktopIntegrationState.Stale
                    : ManagedDesktopIntegrationState.Absent
            );
        }
        catch (OperationCanceledException)
        {
            // Rethrown for callers passing a real token; traced first so an internal probe
            // timeout doesn't fault ScheduleDesktopIntegrationRefresh's task silently.
            System.Diagnostics.Trace.WriteLine(
                "[Shortcuts] Desktop integration status probe was canceled."
            );
            throw;
        }
        catch (Exception ex)
        {
            // An indeterminate probe must not erase a known stale/current state.
            System.Diagnostics.Trace.WriteLine(
                $"[Shortcuts] Desktop integration status probe failed: {ex.Message}"
            );
        }
    }

    private Task ScheduleDesktopIntegrationRefresh()
    {
        var task = RefreshDesktopIntegrationStateAsync(CancellationToken.None);
        _pendingDesktopIntegrationRefresh = task;
        return task;
    }

    private void SetDesktopIntegrationStateIfCurrent(
        int version,
        ManagedDesktopIntegrationState state
    )
    {
        if (version != Volatile.Read(ref _desktopIntegrationRefreshVersion))
        {
            return;
        }

        SetDesktopIntegrationState(state);
    }

    private void SetDesktopIntegrationState(ManagedDesktopIntegrationState state)
    {
        if (_desktopIntegrationState == state)
        {
            return;
        }

        _desktopIntegrationState = state;
        OnPropertyChanged(nameof(DesktopIntegrationState));
        OnPropertyChanged(nameof(ShowStaleIntegrationBanner));
        OnPropertyChanged(nameof(CanRefreshDesktopIntegration));
        OnPropertyChanged(nameof(CanRemoveDesktopIntegration));
        OnPropertyChanged(nameof(StaleIntegrationMessage));
        OnPropertyChanged(nameof(SetupAutomaticallyLabel));
    }

    private void CompleteDesktopIntegrationMutation(
        IDeShortcutWriter writer,
        DeShortcutSpec writtenSpec
    )
    {
        // Invalidate every probe that could have observed the pre-commit state.
        Interlocked.Increment(ref _desktopIntegrationRefreshVersion);
        var currentSpec = BuildSpec(writer);
        if (currentSpec == writtenSpec)
        {
            SetDesktopIntegrationState(ManagedDesktopIntegrationState.Current);
            return;
        }

        _ = ScheduleDesktopIntegrationRefresh();
    }

    private void InvalidateDesktopIntegrationProbes()
    {
        Interlocked.Increment(ref _desktopIntegrationRefreshVersion);
    }

    internal async Task RefreshNativeDictationBindingStateAsync(CancellationToken ct)
    {
        try
        {
            var writer = ActiveWriter;
            var spec = writer is null ? null : BuildSpec(writer);
            if (writer is null || spec is null)
            {
                _hotkey.SetNativeDictationBindingActive(false);
                return;
            }

            var isInstalled = await writer.IsInstalledAsync(spec, ct).ConfigureAwait(false);
            _hotkey.SetNativeDictationBindingActive(isInstalled);
        }
        catch (OperationCanceledException)
        {
            _hotkey.SetNativeDictationBindingActive(false);
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[Shortcuts] Native dictation binding probe failed: {ex.Message}"
            );
            _hotkey.SetNativeDictationBindingActive(false);
        }
    }

    private string GetUnsupportedModeMessage(IDeShortcutWriter writer)
    {
        return Loc.Instance.GetString(
            "Shortcuts.AutoSetupModeUnsupported",
            writer.DisplayName,
            GetModeDisplayName()
        );
    }

    private string GetModeDisplayName()
    {
        return _settings.Current.Mode switch
        {
            RecordingMode.Toggle => Loc.Instance["Common.ModeToggle"],
            RecordingMode.PushToTalk => Loc.Instance["Common.ModePushToTalk"],
            RecordingMode.Hybrid => Loc.Instance["Common.ModeHybrid"],
            _ => ""
        };
    }

    // VMs don't have direct clipboard access in Avalonia — the view
    // subscribes and writes via TopLevel.Clipboard.
    public event EventHandler<string>? CopyCustomShortcutRequested;

    private static bool IsWaylandSession()
    {
        return WaylandSessionDetector.IsWaylandSession();
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

        var spec = BuildSpec(writer);
        if (spec is null)
        {
            IntegrationStatusMessage = GetUnsupportedModeMessage(writer);
            return;
        }

        IntegrationStatusMessage =
            Loc.Instance.GetString("Shortcuts.InstallingShortcut", writer.DisplayName);
        InvalidateDesktopIntegrationProbes();
        try
        {
            var result = await writer
                .WriteAsync(spec, CancellationToken.None)
                .ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);

            if (result.Success)
            {
                var appliesImmediately =
                    !writer.RequiresSessionRestartToApply && result.Warning is null;
                if (appliesImmediately)
                {
                    _hotkey.SetNativeDictationBindingActive(true);
                    IntegrationStatusMessage =
                        $"{IntegrationStatusMessage} "
                        + Loc.Instance["Shortcuts.NativeDictationOwnershipActive"];
                }
                else
                {
                    IntegrationStatusMessage =
                        $"{IntegrationStatusMessage} "
                        + Loc.Instance["Shortcuts.NativeDictationInstallDeferred"];
                }

                CompleteDesktopIntegrationMutation(writer, spec);
            }
            else
            {
                // A write can partially mutate config before failing (e.g. GNOME's managed
                // path added, then gsettings set fails); re-probe so it surfaces as stale, not silent.
                _ = ScheduleDesktopIntegrationRefresh();
            }
        }
        catch (Exception ex)
        {
            _ = ScheduleDesktopIntegrationRefresh();
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
        InvalidateDesktopIntegrationProbes();
        try
        {
            var result = await writer
                .RemoveAsync(DictationShortcutId, CancellationToken.None)
                .ConfigureAwait(true);
            IntegrationStatusMessage = FormatResultMessage(result);

            if (result.Success)
            {
                var appliesImmediately =
                    !writer.RequiresSessionRestartToApply && result.Warning is null;
                if (appliesImmediately)
                {
                    _hotkey.SetNativeDictationBindingActive(false);
                    IntegrationStatusMessage =
                        $"{IntegrationStatusMessage} "
                        + Loc.Instance["Shortcuts.NativeDictationRemovalActive"];
                }
                else
                {
                    IntegrationStatusMessage =
                        $"{IntegrationStatusMessage} "
                        + Loc.Instance["Shortcuts.NativeDictationRemovalDeferred"];
                }

                InvalidateDesktopIntegrationProbes();
                SetDesktopIntegrationState(ManagedDesktopIntegrationState.Absent);
            }
            else
            {
                // A failed removal may leave the managed block partially in place;
                // re-probe rather than trust the pre-removal state.
                _ = ScheduleDesktopIntegrationRefresh();
            }
        }
        catch (Exception ex)
        {
            _ = ScheduleDesktopIntegrationRefresh();
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
        // The user has taken control of the disclosure — auto-tracking stops so a
        // pending/late access probe never overwrites this choice.
        _compositorBindsUserControlled = true;
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
    private async Task ApplyHotkeyAsync()
    {
        if (_hotkey.TrySetHotkeyFromString(HotkeyText))
        {
            _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            StatusMessage = Loc.Instance.GetString("Shortcuts.HotkeySet", _hotkey.CurrentHotkeyString);
            HotkeyText = _hotkey.CurrentHotkeyString;
            OnPropertyChanged(nameof(IntegrationPreview));
            await ScheduleDesktopIntegrationRefresh();
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
        OnPropertyChanged(nameof(IntegrationPreview));
        OnPropertyChanged(nameof(CanWriteDesktopIntegration));
        OnPropertyChanged(nameof(CanRefreshDesktopIntegration));
        OnPropertyChanged(nameof(StaleIntegrationMessage));
        _ = ScheduleDesktopIntegrationRefresh();
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
        // Toggling evdev is a deliberate disclosure reset, so re-arm auto-tracking;
        // this assignment is the optimistic immediate value and the trailing
        // RefreshKeyboardAccessAsync re-applies it with the real probe result.
        _compositorBindsUserControlled = false;
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

        // Re-probe off the UI thread: a switch may follow the user granting access,
        // which must clear the banner. RefreshKeyboardAccessAsync raises the change.
        await RefreshKeyboardAccessAsync();
    }
}
