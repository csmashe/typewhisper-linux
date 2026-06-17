using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AppearanceSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private double _previewBubbleAutoHideSeconds =
        AppSettings.DefaultPreviewBubbleAutoHideMilliseconds / 1000d;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewLeftIsIndicator))]
    [NotifyPropertyChangedFor(nameof(PreviewLeftIsWaveform))]
    [NotifyPropertyChangedFor(nameof(PreviewLeftIsText))]
    [NotifyPropertyChangedFor(nameof(PreviewLeftText))]
    private OverlayWidgetOption? _selectedLeftWidget;

    [ObservableProperty]
    private OverlayPositionOption? _selectedOverlayPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRightIsIndicator))]
    [NotifyPropertyChangedFor(nameof(PreviewRightIsWaveform))]
    [NotifyPropertyChangedFor(nameof(PreviewRightIsText))]
    [NotifyPropertyChangedFor(nameof(PreviewRightText))]
    private OverlayWidgetOption? _selectedRightWidget;

    public AppearanceSectionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh(settings.Current);
        _settings.SettingsChanged += Refresh;
        // Option labels and the localized status/preview getters are resolved into
        // strings, so re-resolve them when the UI language changes at runtime.
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    ///     On tiling window managers the recording indicator is a desktop
    ///     notification, not the overlay — so the overlay layout controls don't
    ///     apply and the view shows the notification panel instead. Cached: the
    ///     desktop can't change within a session.
    /// </summary>
    public bool UsesNotificationIndicator { get; } =
        DesktopDetector.UsesNotificationRecordingIndicator();

    public bool UsesOverlay => !UsesNotificationIndicator;

    /// <summary>Mode-aware body text matching what the notification actually shows.</summary>
    public string NotificationBodyPreview =>
        RecordingNotificationService.BodyFor(_settings.Current.Mode);

    public IReadOnlyList<OverlayPositionOption> OverlayPositions { get; } =
    [
        new(OverlayPosition.Top, "Appearance.PositionTop"),
        new(OverlayPosition.Bottom, "Appearance.PositionBottom")
    ];

    public IReadOnlyList<OverlayWidgetOption> OverlayWidgets { get; } =
    [
        new(OverlayWidget.None, "Appearance.WidgetNone"),
        new(OverlayWidget.Indicator, "Appearance.WidgetIndicator"),
        new(OverlayWidget.Timer, "Appearance.WidgetTimer"),
        new(OverlayWidget.Waveform, "Appearance.WidgetWaveform"),
        new(OverlayWidget.Clock, "Appearance.WidgetClock"),
        new(OverlayWidget.Profile, "Appearance.WidgetProfile"),
        new(OverlayWidget.HotkeyMode, "Appearance.WidgetHotkeyMode"),
        new(OverlayWidget.AppName, "Appearance.WidgetAppName")
    ];

    public string PreviewBubbleAutoHideSecondsText =>
        Loc.Instance.GetString("Appearance.AutoHideSecondsValue", $"{PreviewBubbleAutoHideSeconds:0.##}");

    public bool IsOverlayPositionCustomized =>
        _settings.Current.OverlayCustomLeft is not null
        && _settings.Current.OverlayCustomTop is not null;

    public string OverlayPositionStatusText =>
        IsOverlayPositionCustomized
            ? Loc.Instance.GetString(
                "Appearance.CustomPositionStatus",
                (int)Math.Round(_settings.Current.OverlayCustomLeft ?? 0),
                (int)Math.Round(_settings.Current.OverlayCustomTop ?? 0))
            : Loc.Instance["Appearance.UsingDefaultPosition"];

    public bool PreviewLeftIsIndicator => SelectedLeftWidget?.Value == OverlayWidget.Indicator;
    public bool PreviewLeftIsWaveform => SelectedLeftWidget?.Value == OverlayWidget.Waveform;
    public bool PreviewLeftIsText => IsTextWidget(SelectedLeftWidget?.Value);
    public string PreviewLeftText => SampleText(SelectedLeftWidget?.Value);

    public bool PreviewRightIsIndicator => SelectedRightWidget?.Value == OverlayWidget.Indicator;
    public bool PreviewRightIsWaveform => SelectedRightWidget?.Value == OverlayWidget.Waveform;
    public bool PreviewRightIsText => IsTextWidget(SelectedRightWidget?.Value);
    public string PreviewRightText => SampleText(SelectedRightWidget?.Value);

    [RelayCommand]
    private void ResetOverlayPosition()
    {
        if (!IsOverlayPositionCustomized)
        {
            return;
        }

        _settings.Save(_settings.Current with { OverlayCustomLeft = null, OverlayCustomTop = null });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var option in OverlayPositions)
        {
            option.RefreshDisplayName();
        }

        foreach (var option in OverlayWidgets)
        {
            option.RefreshDisplayName();
        }

        // The remaining localized labels in this view are computed getters bound
        // via {Binding}, so nudge them to re-read from Loc in the new language.
        OnPropertyChanged(nameof(NotificationBodyPreview));
        OnPropertyChanged(nameof(PreviewBubbleAutoHideSecondsText));
        OnPropertyChanged(nameof(OverlayPositionStatusText));
        OnPropertyChanged(nameof(PreviewLeftText));
        OnPropertyChanged(nameof(PreviewRightText));
    }

    private void Refresh(AppSettings settings)
    {
        SelectedOverlayPosition =
            OverlayPositions.FirstOrDefault(option => option.Value == settings.OverlayPosition)
            ?? OverlayPositions[0];
        SelectedLeftWidget =
            OverlayWidgets.FirstOrDefault(option => option.Value == settings.OverlayLeftWidget)
            ?? OverlayWidgets[0];
        SelectedRightWidget =
            OverlayWidgets.FirstOrDefault(option => option.Value == settings.OverlayRightWidget)
            ?? OverlayWidgets[0];
        PreviewBubbleAutoHideSeconds =
            AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                settings.PreviewBubbleAutoHideMilliseconds) / 1000d;

        OnPropertyChanged(nameof(IsOverlayPositionCustomized));
        OnPropertyChanged(nameof(OverlayPositionStatusText));
        ResetOverlayPositionCommand.NotifyCanExecuteChanged();

        // Mode changes elsewhere don't flip the selected widget, but HotkeyMode
        // preview text and the notification body preview still need to refresh.
        OnPropertyChanged(nameof(PreviewLeftText));
        OnPropertyChanged(nameof(PreviewRightText));
        OnPropertyChanged(nameof(NotificationBodyPreview));
    }

    private static bool IsTextWidget(OverlayWidget? widget)
    {
        return widget
            is OverlayWidget.Timer
            or OverlayWidget.Clock
            or OverlayWidget.Profile
            or OverlayWidget.HotkeyMode
            or OverlayWidget.AppName;
    }

    private string SampleText(OverlayWidget? widget)
    {
        return widget switch
        {
            OverlayWidget.Timer => "0:05",
            OverlayWidget.Clock => "10:24",
            OverlayWidget.Profile => Loc.Instance["Appearance.SampleProfile"],
            OverlayWidget.HotkeyMode => _settings.Current.Mode switch
            {
                RecordingMode.Toggle => Loc.Instance["Common.ModeToggle"],
                RecordingMode.PushToTalk => Loc.Instance["Common.ModePushToTalk"],
                RecordingMode.Hybrid => Loc.Instance["Common.ModeHybrid"],
                _ => ""
            },
            OverlayWidget.AppName => Loc.Instance["Appearance.SampleAppName"],
            _ => ""
        };
    }

    partial void OnSelectedOverlayPositionChanged(OverlayPositionOption? value)
    {
        if (value is null || _settings.Current.OverlayPosition == value.Value)
        {
            return;
        }

        _settings.Save(_settings.Current with { OverlayPosition = value.Value });
    }

    partial void OnSelectedLeftWidgetChanged(OverlayWidgetOption? value)
    {
        if (value is null || _settings.Current.OverlayLeftWidget == value.Value)
        {
            return;
        }

        _settings.Save(_settings.Current with { OverlayLeftWidget = value.Value });
    }

    partial void OnSelectedRightWidgetChanged(OverlayWidgetOption? value)
    {
        if (value is null || _settings.Current.OverlayRightWidget == value.Value)
        {
            return;
        }

        _settings.Save(_settings.Current with { OverlayRightWidget = value.Value });
    }

    partial void OnPreviewBubbleAutoHideSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewBubbleAutoHideSecondsText));

        var milliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            (int)Math.Round(value * 1000, MidpointRounding.AwayFromZero));

        if (_settings.Current.PreviewBubbleAutoHideMilliseconds == milliseconds)
        {
            return;
        }

        _settings.Save(
            _settings.Current with { PreviewBubbleAutoHideMilliseconds = milliseconds });
    }
}

// Stores the localization key (not the resolved string) so DisplayName can be
// re-resolved on a live UI-language switch. Item instances stay stable, so the
// ComboBox selection is preserved — only the rendered text changes.
public sealed partial class OverlayPositionOption(OverlayPosition value, string displayNameKey)
    : ObservableObject
{
    public OverlayPosition Value { get; } = value;
    public string DisplayName => Loc.Instance[displayNameKey];

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

public sealed partial class OverlayWidgetOption(OverlayWidget value, string displayNameKey)
    : ObservableObject
{
    public OverlayWidget Value { get; } = value;
    public string DisplayName => Loc.Instance[displayNameKey];

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}