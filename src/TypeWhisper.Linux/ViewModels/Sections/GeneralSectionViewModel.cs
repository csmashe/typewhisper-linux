using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly LinuxPreferencesService _linuxPrefs;

    [ObservableProperty] private bool _soundFeedbackEnabled;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _saveToHistoryEnabled;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private HistoryRetentionOption? _selectedHistoryRetention;

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];
    public IReadOnlyList<HistoryRetentionOption> HistoryRetentionOptions { get; } =
    [
        new(HistoryRetentionMode.Duration, 24 * 60, "1 day"),
        new(HistoryRetentionMode.Duration, 7 * 24 * 60, "7 days"),
        new(HistoryRetentionMode.Duration, 30 * 24 * 60, "30 days"),
        new(HistoryRetentionMode.Duration, 90 * 24 * 60, "90 days"),
        new(HistoryRetentionMode.Forever, null, "Forever"),
        new(HistoryRetentionMode.UntilAppCloses, null, "Until app closes")
    ];

    public GeneralSectionViewModel(ISettingsService settings, LinuxPreferencesService linuxPrefs)
    {
        _settings = settings;
        _linuxPrefs = linuxPrefs;
        Refresh(settings.Current);
        CloseToTray = _linuxPrefs.Current.CloseToTray;
        StartWithSystem = StartupService.IsEnabled;
        _settings.SettingsChanged += Refresh;
    }

    private void Refresh(Core.Models.AppSettings s)
    {
        SoundFeedbackEnabled = s.SoundFeedbackEnabled;
        AutoPaste = s.AutoPaste;
        SaveToHistoryEnabled = s.SaveToHistoryEnabled;
        Language = string.IsNullOrEmpty(s.Language) ? "auto" : s.Language;
        SelectedHistoryRetention = MatchRetention(s.HistoryRetentionMode, s.HistoryRetentionMinutes);
    }

    partial void OnSoundFeedbackEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SoundFeedbackEnabled = value });

    partial void OnAutoPasteChanged(bool value)
        => _settings.Save(_settings.Current with { AutoPaste = value });

    partial void OnSaveToHistoryEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SaveToHistoryEnabled = value });

    partial void OnLanguageChanged(string value)
        => _settings.Save(_settings.Current with { Language = value });

    partial void OnCloseToTrayChanged(bool value)
        => _linuxPrefs.Save(_linuxPrefs.Current with { CloseToTray = value });

    partial void OnStartWithSystemChanged(bool value)
    {
        if (value == StartupService.IsEnabled)
            return;

        if (value)
            StartupService.Enable();
        else
            StartupService.Disable();
    }

    partial void OnSelectedHistoryRetentionChanged(HistoryRetentionOption? value)
    {
        if (value is null) return;

        if (_settings.Current.HistoryRetentionMode == value.Mode
            && (value.Mode != HistoryRetentionMode.Duration
                || _settings.Current.HistoryRetentionMinutes == value.Minutes))
            return;

        _settings.Save(_settings.Current with
        {
            HistoryRetentionMode = value.Mode,
            HistoryRetentionMinutes = value.Minutes ?? _settings.Current.HistoryRetentionMinutes
        });
    }

    private HistoryRetentionOption MatchRetention(HistoryRetentionMode mode, int minutes) =>
        HistoryRetentionOptions.FirstOrDefault(option =>
            option.Mode == mode && (mode != HistoryRetentionMode.Duration || option.Minutes == minutes))
        ?? HistoryRetentionOptions.First(option =>
            option.Mode == AppSettings.Default.HistoryRetentionMode
            && option.Minutes == AppSettings.Default.HistoryRetentionMinutes);
}

public sealed record HistoryRetentionOption(
    HistoryRetentionMode Mode,
    int? Minutes,
    string DisplayName);
