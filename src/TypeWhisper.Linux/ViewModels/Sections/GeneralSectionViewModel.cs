using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    [ObservableProperty] private string? _uiLanguage;
    [ObservableProperty] private bool _startWithSystem;
    [ObservableProperty] private bool _apiServerEnabled;
    [ObservableProperty] private int _apiServerPort;

    public IReadOnlyList<UiLanguageOption> UiLanguageChoices { get; } =
    [
        new(null, "Auto (System)"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("pt", "Português"),
        new("ja", "日本語"),
        new("zh", "中文"),
        new("ko", "한국어"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("ru", "Русский")
    ];

    public bool IsUiLanguageSupported => false;
    public string UiLanguageSupportMessage => "Interface language is not implemented in the Linux build yet.";

    public UiLanguageOption? SelectedUiLanguageOption
    {
        get => UiLanguageChoices.FirstOrDefault(option =>
            string.Equals(option.Value, UiLanguage, StringComparison.Ordinal));
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, UiLanguage, StringComparison.Ordinal))
                return;

            UiLanguage = selected;
            OnPropertyChanged();
        }
    }

    public GeneralSectionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh(settings.Current);
        StartWithSystem = StartupService.IsEnabled;
        _settings.SettingsChanged += Refresh;
    }

    private void Refresh(Core.Models.AppSettings s)
    {
        UiLanguage = s.UiLanguage;
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    partial void OnUiLanguageChanged(string? value)
    {
        _settings.Save(_settings.Current with { UiLanguage = value });
        OnPropertyChanged(nameof(SelectedUiLanguageOption));
    }

    partial void OnStartWithSystemChanged(bool value)
    {
        if (value == StartupService.IsEnabled)
            return;

        if (value)
            StartupService.Enable();
        else
            StartupService.Disable();
    }

    partial void OnApiServerEnabledChanged(bool value)
    {
        if (_settings.Current.ApiServerEnabled == value)
            return;

        _settings.Save(_settings.Current with { ApiServerEnabled = value });
    }

    partial void OnApiServerPortChanged(int value)
    {
        if (value <= 0 || value > 65535 || _settings.Current.ApiServerPort == value)
            return;

        _settings.Save(_settings.Current with { ApiServerPort = value });
    }
}

public sealed record UiLanguageOption(string? Value, string DisplayName);
