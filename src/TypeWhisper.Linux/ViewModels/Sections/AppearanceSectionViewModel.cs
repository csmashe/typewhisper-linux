using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AppearanceSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

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
    }

    public IReadOnlyList<OverlayPositionOption> OverlayPositions { get; } =
        [new(OverlayPosition.Top, "Top"), new(OverlayPosition.Bottom, "Bottom")];

    public IReadOnlyList<OverlayWidgetOption> OverlayWidgets { get; } =
    [
        new(OverlayWidget.None, "None"),
        new(OverlayWidget.Indicator, "Indicator"),
        new(OverlayWidget.Timer, "Timer"),
        new(OverlayWidget.Waveform, "Waveform"),
        new(OverlayWidget.Clock, "Clock"),
        new(OverlayWidget.Profile, "Profile"),
        new(OverlayWidget.HotkeyMode, "Hotkey mode"),
        new(OverlayWidget.AppName, "App name")
    ];

    public bool PreviewLeftIsIndicator => SelectedLeftWidget?.Value == OverlayWidget.Indicator;
    public bool PreviewLeftIsWaveform => SelectedLeftWidget?.Value == OverlayWidget.Waveform;
    public bool PreviewLeftIsText => IsTextWidget(SelectedLeftWidget?.Value);
    public string PreviewLeftText => SampleText(SelectedLeftWidget?.Value);

    public bool PreviewRightIsIndicator => SelectedRightWidget?.Value == OverlayWidget.Indicator;
    public bool PreviewRightIsWaveform => SelectedRightWidget?.Value == OverlayWidget.Waveform;
    public bool PreviewRightIsText => IsTextWidget(SelectedRightWidget?.Value);
    public string PreviewRightText => SampleText(SelectedRightWidget?.Value);

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

        // Mode changes elsewhere don't flip the selected widget, but HotkeyMode
        // preview text still needs to refresh.
        OnPropertyChanged(nameof(PreviewLeftText));
        OnPropertyChanged(nameof(PreviewRightText));
    }

    private static bool IsTextWidget(OverlayWidget? widget) =>
        widget
            is OverlayWidget.Timer
                or OverlayWidget.Clock
                or OverlayWidget.Profile
                or OverlayWidget.HotkeyMode
                or OverlayWidget.AppName;

    private string SampleText(OverlayWidget? widget) =>
        widget switch
        {
            OverlayWidget.Timer => "0:05",
            OverlayWidget.Clock => "10:24",
            OverlayWidget.Profile => "Default profile",
            OverlayWidget.HotkeyMode => _settings.Current.Mode switch
            {
                RecordingMode.Toggle => "Toggle",
                RecordingMode.PushToTalk => "Push to talk",
                RecordingMode.Hybrid => "Hybrid",
                _ => ""
            },
            OverlayWidget.AppName => "Sample app",
            _ => ""
        };

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
}

public sealed record OverlayPositionOption(OverlayPosition Value, string DisplayName);

public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);