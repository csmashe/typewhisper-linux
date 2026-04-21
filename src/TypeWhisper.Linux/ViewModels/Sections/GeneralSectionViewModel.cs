using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class GeneralSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty] private bool _soundFeedbackEnabled;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _saveToHistoryEnabled;
    [ObservableProperty] private string _language = "auto";

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];

    public GeneralSectionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh(settings.Current);
        _settings.SettingsChanged += Refresh;
    }

    private void Refresh(Core.Models.AppSettings s)
    {
        SoundFeedbackEnabled = s.SoundFeedbackEnabled;
        AutoPaste = s.AutoPaste;
        SaveToHistoryEnabled = s.SaveToHistoryEnabled;
        Language = string.IsNullOrEmpty(s.Language) ? "auto" : s.Language;
    }

    partial void OnSoundFeedbackEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SoundFeedbackEnabled = value });

    partial void OnAutoPasteChanged(bool value)
        => _settings.Save(_settings.Current with { AutoPaste = value });

    partial void OnSaveToHistoryEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SaveToHistoryEnabled = value });

    partial void OnLanguageChanged(string value)
        => _settings.Save(_settings.Current with { Language = value });
}
