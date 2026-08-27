using Avalonia.Threading;
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
    private readonly Action<Action> _post;
    private readonly ISettingsService _settings;

    // Set while Refresh applies persisted state so the generated On<Property>Changed hooks don't
    // write it straight back: their equality guards compare against _settings.Current, which a
    // queued refresh has already fallen behind, so a stale value would overwrite the newer commit.
    private bool _hydratingFromSettings;

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

    // post marshals refreshes onto the UI thread; it is injected rather than calling
    // Dispatcher.UIThread directly because that dispatcher binds to whichever thread touches it
    // first and nothing pumps it under the test runner, so tests pass a synchronous one.
    public AppearanceSectionViewModel(ISettingsService settings, Action<Action>? post = null)
    {
        _settings = settings;
        _post = post ?? PostToUiThread;
        Refresh(settings.Current);
        _settings.SettingsChanged += OnSettingsChanged;
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
        new(OverlayPosition.Bottom, "Appearance.PositionBottom"),
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
        new(OverlayWidget.AppName, "Appearance.WidgetAppName"),
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

        _settings.Update(current => current with { OverlayCustomLeft = null, OverlayCustomTop = null });
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

    // Saves happen on whichever thread called them — the dictation path and the model-storage
    // migration both save off the UI thread — and Refresh writes bound properties.
    private void OnSettingsChanged(AppSettings settings)
    {
        // Read Current when the post runs rather than capturing the payload, so queued
        // refreshes coalesce onto the newest commit instead of replaying superseded ones.
        _post(() => Refresh(_settings.Current));
    }

    private static void PostToUiThread(Action action)
    {
        // Inline when already on the UI thread, so a save from the UI keeps refreshing
        // synchronously rather than deferring to the next dispatcher turn.
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void Refresh(AppSettings settings)
    {
        // Restore rather than clear: a nested Refresh must not un-guard the remainder
        // of the outer one, which would let it write its older snapshot back.
        var wasHydrating = _hydratingFromSettings;
        _hydratingFromSettings = true;
        try
        {
            ApplySettings(settings);
        }
        finally
        {
            _hydratingFromSettings = wasHydrating;
        }
    }

    private void ApplySettings(AppSettings settings)
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
                _ => "",
            },
            OverlayWidget.AppName => Loc.Instance["Appearance.SampleAppName"],
            _ => "",
        };
    }

    partial void OnSelectedOverlayPositionChanged(OverlayPositionOption? value)
    {
        if (_hydratingFromSettings || value is null || _settings.Current.OverlayPosition == value.Value)
        {
            return;
        }

        _settings.Update(current => current with { OverlayPosition = value.Value });
    }

    partial void OnSelectedLeftWidgetChanged(OverlayWidgetOption? value)
    {
        if (_hydratingFromSettings || value is null || _settings.Current.OverlayLeftWidget == value.Value)
        {
            return;
        }

        _settings.Update(current => current with { OverlayLeftWidget = value.Value });
    }

    partial void OnSelectedRightWidgetChanged(OverlayWidgetOption? value)
    {
        if (_hydratingFromSettings || value is null || _settings.Current.OverlayRightWidget == value.Value)
        {
            return;
        }

        _settings.Update(current => current with { OverlayRightWidget = value.Value });
    }

    partial void OnPreviewBubbleAutoHideSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewBubbleAutoHideSecondsText));

        var milliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            (int)Math.Round(value * 1000, MidpointRounding.AwayFromZero));

        if (_hydratingFromSettings
            || _settings.Current.PreviewBubbleAutoHideMilliseconds == milliseconds)
        {
            return;
        }

        _settings.Update(current =>
            current with { PreviewBubbleAutoHideMilliseconds = milliseconds });
    }
}

// Stores the localization key (not the resolved string) so DisplayName can be
// re-resolved on a live UI-language switch. Item instances stay stable, so the
// ComboBox selection is preserved — only the rendered text changes.
public sealed class OverlayPositionOption(OverlayPosition value, string displayNameKey)
    : ObservableObject
{
    public OverlayPosition Value { get; } = value;
    public string DisplayName => Loc.Instance[displayNameKey];

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

public sealed class OverlayWidgetOption(OverlayWidget value, string displayNameKey)
    : ObservableObject
{
    public OverlayWidget Value { get; } = value;
    public string DisplayName => Loc.Instance[displayNameKey];

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}
