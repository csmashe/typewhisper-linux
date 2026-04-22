using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AdvancedSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty] private bool _memoryEnabled;
    [ObservableProperty] private AutoUnloadOption? _selectedAutoUnloadOption;
    [ObservableProperty] private bool _spokenFeedbackEnabled;
    [ObservableProperty] private bool _saveToHistoryEnabled;
    [ObservableProperty] private HistoryRetentionOption? _selectedHistoryRetention;

    public IReadOnlyList<AutoUnloadOption> AutoUnloadOptions { get; } =
    [
        new(0, "Never"),
        new(30, "30 seconds"),
        new(60, "1 minute"),
        new(300, "5 minutes"),
        new(900, "15 minutes")
    ];

    public IReadOnlyList<HistoryRetentionOption> HistoryRetentionOptions { get; } =
    [
        new(HistoryRetentionMode.Duration, 24 * 60, "1 day"),
        new(HistoryRetentionMode.Duration, 7 * 24 * 60, "7 days"),
        new(HistoryRetentionMode.Duration, 30 * 24 * 60, "30 days"),
        new(HistoryRetentionMode.Duration, 90 * 24 * 60, "90 days"),
        new(HistoryRetentionMode.Forever, null, "Forever"),
        new(HistoryRetentionMode.UntilAppCloses, null, "Until app closes")
    ];

    public AdvancedSectionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh(settings.Current);
        _settings.SettingsChanged += Refresh;
    }

    private void Refresh(AppSettings settings)
    {
        MemoryEnabled = settings.MemoryEnabled;
        SpokenFeedbackEnabled = settings.SpokenFeedbackEnabled;
        SaveToHistoryEnabled = settings.SaveToHistoryEnabled;
        SelectedAutoUnloadOption = AutoUnloadOptions.FirstOrDefault(option => option.Seconds == settings.ModelAutoUnloadSeconds)
            ?? AutoUnloadOptions[0];
        SelectedHistoryRetention = MatchRetention(settings.HistoryRetentionMode, settings.HistoryRetentionMinutes);
    }

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_settings.Current.MemoryEnabled == value)
            return;

        _settings.Save(_settings.Current with { MemoryEnabled = value });
    }

    partial void OnSelectedAutoUnloadOptionChanged(AutoUnloadOption? value)
    {
        if (value is null || _settings.Current.ModelAutoUnloadSeconds == value.Seconds)
            return;

        _settings.Save(_settings.Current with { ModelAutoUnloadSeconds = value.Seconds });
    }

    partial void OnSpokenFeedbackEnabledChanged(bool value)
    {
        if (_settings.Current.SpokenFeedbackEnabled == value)
            return;

        _settings.Save(_settings.Current with { SpokenFeedbackEnabled = value });
    }

    partial void OnSaveToHistoryEnabledChanged(bool value)
    {
        if (_settings.Current.SaveToHistoryEnabled == value)
            return;

        _settings.Save(_settings.Current with { SaveToHistoryEnabled = value });
    }

    partial void OnSelectedHistoryRetentionChanged(HistoryRetentionOption? value)
    {
        if (value is null)
            return;

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

public sealed record AutoUnloadOption(int Seconds, string DisplayName);

public sealed record HistoryRetentionOption(
    HistoryRetentionMode Mode,
    int? Minutes,
    string DisplayName);
