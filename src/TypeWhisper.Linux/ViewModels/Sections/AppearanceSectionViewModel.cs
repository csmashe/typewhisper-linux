using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class AppearanceSectionViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private OverlayWidgetOption? _selectedLeftWidget;

    [ObservableProperty]
    private OverlayPositionOption? _selectedOverlayPosition;

    [ObservableProperty]
    private OverlayWidgetOption? _selectedRightWidget;

    [ObservableProperty]
    private double _previewBubbleAutoHideSeconds =
        AppSettings.DefaultPreviewBubbleAutoHideMilliseconds / 1000d;

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

    public string PreviewBubbleAutoHideSecondsText => $"{PreviewBubbleAutoHideSeconds:0.##} s";

    public bool IsOverlayPositionCustomized =>
        _settings.Current.OverlayCustomLeft is not null
        && _settings.Current.OverlayCustomTop is not null;

    public string OverlayPositionStatusText =>
        IsOverlayPositionCustomized
            ? $"Custom position: {(int)Math.Round(_settings.Current.OverlayCustomLeft ?? 0)}, {(int)Math.Round(_settings.Current.OverlayCustomTop ?? 0)}"
            : "Using default (Top/Bottom)";

    [RelayCommand]
    private void ResetOverlayPosition()
    {
        if (!IsOverlayPositionCustomized)
        {
            return;
        }

        _settings.Save(_settings.Current with
        {
            OverlayCustomLeft = null,
            OverlayCustomTop = null,
        });
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

public sealed record OverlayPositionOption(OverlayPosition Value, string DisplayName);

public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);