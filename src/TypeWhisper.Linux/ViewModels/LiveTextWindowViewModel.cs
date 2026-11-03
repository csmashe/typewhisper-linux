using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels;

/// <summary>
///     Content of the floating live-text window: the overlay's live text while dictating, or sample
///     text while the Appearance preview runs. Dictation takes over from (and ends) the preview.
/// </summary>
public sealed class LiveTextWindowViewModel : ObservableObject
{
    private readonly DictationOverlayViewModel _overlay;
    private readonly LiveTextPreviewService _preview;
    private readonly ISettingsService _settings;
    private bool _hasVisibleContent;

    public LiveTextWindowViewModel(
        DictationOverlayViewModel overlay,
        ISettingsService settings,
        LiveTextPreviewService preview,
        Action<Action> postToUiThread
    )
    {
        _overlay = overlay;
        _settings = settings;
        _preview = preview;
        _overlay.PropertyChanged += OnOverlayPropertyChanged;
        _preview.Changed += (_, _) => OnPreviewChanged();
        _settings.SettingsChanged += _ => postToUiThread(Refresh);
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    public bool IsPreview => !HasLiveText && _preview.IsActive;

    public string? Text => HasLiveText ? _overlay.PartialText : IsPreview ? Loc.Instance["LiveText.PreviewSample"] : null;

    public string HeaderText => Loc.Instance[IsPreview ? "LiveText.PreviewHeader" : "LiveText.Header"];

    public bool HasVisibleContent => _settings.Current.LiveTextFloating && !string.IsNullOrEmpty(Text);

    private bool HasLiveText => _overlay.IsOverlayVisible && !string.IsNullOrEmpty(_overlay.PartialText);

    /// <summary>Ends the preview; live dictation text stays until the overlay hides it.</summary>
    public void Dismiss()
    {
        _preview.Stop();
    }

    // Dictation owns the window: a preview started during it would return when it ends.
    private void OnPreviewChanged()
    {
        if (_preview.IsActive && _overlay.IsOverlayVisible)
        {
            _preview.Stop();
            return;
        }

        Refresh();
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DictationOverlayViewModel.IsOverlayVisible):
                if (_overlay.IsOverlayVisible)
                {
                    _preview.Stop();
                }

                Refresh();
                break;
            case nameof(DictationOverlayViewModel.PartialText):
                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsPreview));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(HeaderText));

        // Raised on real changes only: the window treats it as a show/hide transition.
        if (_hasVisibleContent == HasVisibleContent)
        {
            return;
        }

        _hasVisibleContent = HasVisibleContent;
        OnPropertyChanged(nameof(HasVisibleContent));
    }
}
