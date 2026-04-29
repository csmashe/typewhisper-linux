using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ShortcutsSectionViewModel : ObservableObject
{
    private readonly HotkeyService _hotkey;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _promptPaletteHotkeyText = "";
    [ObservableProperty] private string _recentTranscriptionsHotkeyText = "";
    [ObservableProperty] private string _copyLastTranscriptionHotkeyText = "";
    [ObservableProperty] private string _transformSelectionHotkeyText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private RecordingMode _mode;

    public IReadOnlyList<RecordingMode> Modes { get; } =
        [RecordingMode.Toggle, RecordingMode.PushToTalk, RecordingMode.Hybrid];

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
    }

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
    }
}
