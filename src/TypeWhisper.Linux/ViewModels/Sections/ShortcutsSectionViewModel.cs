using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ShortcutsSectionViewModel : ObservableObject
{
    private readonly HotkeyService _hotkey;
    private readonly ISettingsService _settings;

    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _statusMessage = "";

    public ShortcutsSectionViewModel(HotkeyService hotkey, ISettingsService settings)
    {
        _hotkey = hotkey;
        _settings = settings;
        HotkeyText = _hotkey.CurrentHotkeyString;
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
}
