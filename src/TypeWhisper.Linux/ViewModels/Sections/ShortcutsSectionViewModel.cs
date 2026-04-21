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
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private RecordingMode _mode;

    public IReadOnlyList<RecordingMode> Modes { get; } =
        [RecordingMode.Toggle, RecordingMode.PushToTalk, RecordingMode.Hybrid];

    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
    {
        _hotkey = hotkey;
        _settings = settings;
        HotkeyText = _hotkey.CurrentHotkeyString;
        Mode = settings.Current.Mode;
    }

    [RelayCommand]
    private void ApplyHotkey()
    {
        if (_hotkey.TrySetHotkeyFromString(HotkeyText))
        {
            _settings.Save(_settings.Current with { ToggleHotkey = HotkeyText });
            StatusMessage = $"Hotkey set to {_hotkey.CurrentHotkeyString}.";
            HotkeyText = _hotkey.CurrentHotkeyString;
        }
        else
        {
            StatusMessage = $"Could not parse '{HotkeyText}'. Try e.g. Ctrl+Shift+Space, Alt+F9, Ctrl+K.";
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
            RecordingMode.Hybrid => "Short press: toggle. Hold past ~600 ms: push-to-talk.",
            _ => "",
        };
    }
}
