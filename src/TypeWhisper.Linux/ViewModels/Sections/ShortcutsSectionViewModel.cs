using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.Evdev;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ShortcutsSectionViewModel : ObservableObject
{
    private const string AddToInputGroupCommand = "sudo usermod -aG input $USER";

    private readonly HotkeyService _hotkey;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _promptPaletteHotkeyText = "";
    [ObservableProperty] private string _recentTranscriptionsHotkeyText = "";
    [ObservableProperty] private string _copyLastTranscriptionHotkeyText = "";
    [ObservableProperty] private string _transformSelectionHotkeyText = "";
    [ObservableProperty] private string _statusMessage = "";
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

    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
    {
        _hotkey = hotkey;
        _settings = settings;
        HotkeyText = _hotkey.CurrentHotkeyString;
        PromptPaletteHotkeyText = settings.Current.PromptPaletteHotkey;
        RecentTranscriptionsHotkeyText = settings.Current.RecentTranscriptionsHotkey;
        CopyLastTranscriptionHotkeyText = settings.Current.CopyLastTranscriptionHotkey;
        TransformSelectionHotkeyText = settings.Current.TransformSelectionHotkey;
        Mode = settings.Current.Mode;
        _waylandEvdevHotkeysEnabled = settings.Current.WaylandEvdevHotkeysEnabled;
    }

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
        _ = SwitchBackendAndNotifyAsync();
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
