using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AppearanceSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty] private OverlayPositionOption? _selectedOverlayPosition;
    [ObservableProperty] private OverlayWidgetOption? _selectedLeftWidget;
    [ObservableProperty] private OverlayWidgetOption? _selectedRightWidget;

    public IReadOnlyList<OverlayPositionOption> OverlayPositions { get; } =
    [
        new(OverlayPosition.Top, "Top"),
        new(OverlayPosition.Bottom, "Bottom")
    ];

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

    public AppearanceSectionViewModel(ISettingsService settings)
    {
        _settings = settings;
        Refresh(settings.Current);
        _settings.SettingsChanged += Refresh;
    }

    private void Refresh(AppSettings settings)
    {
        SelectedOverlayPosition = OverlayPositions.FirstOrDefault(option => option.Value == settings.OverlayPosition)
            ?? OverlayPositions[0];
        SelectedLeftWidget = OverlayWidgets.FirstOrDefault(option => option.Value == settings.OverlayLeftWidget)
            ?? OverlayWidgets[0];
        SelectedRightWidget = OverlayWidgets.FirstOrDefault(option => option.Value == settings.OverlayRightWidget)
            ?? OverlayWidgets[0];
    }

    partial void OnSelectedOverlayPositionChanged(OverlayPositionOption? value)
    {
        if (value is null || _settings.Current.OverlayPosition == value.Value)
            return;

        _settings.Save(_settings.Current with { OverlayPosition = value.Value });
    }

    partial void OnSelectedLeftWidgetChanged(OverlayWidgetOption? value)
    {
        if (value is null || _settings.Current.OverlayLeftWidget == value.Value)
            return;

        _settings.Save(_settings.Current with { OverlayLeftWidget = value.Value });
    }

    partial void OnSelectedRightWidgetChanged(OverlayWidgetOption? value)
    {
        if (value is null || _settings.Current.OverlayRightWidget == value.Value)
            return;

        _settings.Save(_settings.Current with { OverlayRightWidget = value.Value });
    }
}

public sealed record OverlayPositionOption(OverlayPosition Value, string DisplayName);

public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);
