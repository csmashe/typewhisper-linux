using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class DictationOverlayWindow : Window
{
    private readonly ISettingsService? _settings;
    private readonly DictationOverlayViewModel? _viewModel;
    private bool _userDragging;
    private bool _programmaticPositionChange;
    private DispatcherTimer? _dragSaveTimer;
    private PixelPoint? _pendingDragPosition;

    public DictationOverlayWindow()
    {
        InitializeComponent();
    }

    public DictationOverlayWindow(DictationOverlayViewModel viewModel, ISettingsService settings)
        : this()
    {
        DataContext = _viewModel = viewModel;
        _settings = settings;

        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;

        Title = "TypeWhisper Overlay";

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(PositionOverlay);

        Opened += (_, _) => PositionOverlay();
        SizeChanged += (_, _) => PositionOverlay();

        PointerPressed += OnUserPointerPressed;
        PointerReleased += OnUserPointerReleased;
        PointerCaptureLost += OnUserPointerCaptureLost;
        PositionChanged += OnUserPositionChanged;
    }

    public void Initialize()
    {
        if (_viewModel is null)
        {
            return;
        }

        UpdateWindowVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DictationOverlayViewModel.HasVisibleContent))
        {
            Dispatcher.UIThread.Post(UpdateWindowVisibility);
        }
        else if (e.PropertyName == nameof(DictationOverlayViewModel.PartialText))
        {
            // Keep the newest recognized text in view as the live preview
            // grows. Deferred to Background priority so the scroll runs
            // after layout has measured the updated text, matching the
            // after-layout pattern used for PositionOverlay above.
            var partialText = _viewModel?.PartialText;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (string.IsNullOrWhiteSpace(partialText))
                    {
                        PartialPreviewScrollViewer?.ScrollToHome();
                    }
                    else
                    {
                        PartialPreviewScrollViewer?.ScrollToEnd();
                    }
                },
                DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(DictationOverlayViewModel.LlmResponseText))
        {
            // Same after-layout auto-scroll as PartialText, for the streamed
            // LLM response area.
            var llmText = _viewModel?.LlmResponseText;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (string.IsNullOrWhiteSpace(llmText))
                    {
                        LlmResponseScrollViewer?.ScrollToHome();
                    }
                    else
                    {
                        LlmResponseScrollViewer?.ScrollToEnd();
                    }
                },
                DispatcherPriority.Background);
        }
    }

    private void UpdateWindowVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        // On tiling window managers the overlay is suppressed entirely — a
        // desktop notification (RecordingNotificationService) is the recording
        // indicator there. An XWayland toplevel on a tiler reserves a tile,
        // steals focus from the dictation target, and blurs into a box, none of
        // which we can cleanly avoid — so keep it unmapped.
        if (UsesNotificationIndicator)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        // Desktop environments (GNOME / KDE / Cinnamon / …): the overlay works
        // well and stays exactly as it was.
        //
        // WORKAROUND (docs/plans/2026-05-13-linux-backlog.md item 16):
        // Show() once and never Hide() — Avalonia's Window.Show() after a prior
        // Hide() is unreliable on GNOME Mutter for these utility-window flags
        // (ShowActivated=False / Topmost / ShowInTaskbar=False): some shows
        // leave the window invisible until restart. Driving visibility via
        // Opacity keeps the window alive and avoids the race. The inner Border
        // bindings still handle which content is drawn, and a fully transparent
        // surface is essentially free.
        var hasContent = _viewModel.HasVisibleContent;

        if (!IsVisible)
        {
            Show();
        }

        Opacity = hasContent ? 1.0 : 0.0;
        IsHitTestVisible = hasContent;

        if (hasContent)
        {
            Dispatcher.UIThread.Post(PositionOverlay, DispatcherPriority.Loaded);
        }
    }

    // Tiling WMs (Hyprland/Sway/…) use the notification indicator instead of
    // this overlay. Cached — the desktop can't change within a session.
    private static readonly bool UsesNotificationIndicator =
        DesktopDetector.UsesNotificationRecordingIndicator();

    private void PositionOverlay()
    {
        if (!IsVisible || _settings is null)
        {
            return;
        }

        // Don't fight the user while they're dragging the overlay
        // — a SizeChanged mid-drag would otherwise yank the window.
        if (_userDragging)
        {
            return;
        }

        var primaryScreen = Screens?.Primary;
        if (primaryScreen is null)
        {
            return;
        }

        var width = Math.Max(320, Bounds.Width);
        var height = Math.Max(56, Bounds.Height);

        if (_settings.Current.OverlayCustomLeft is { } customLeft &&
            _settings.Current.OverlayCustomTop is { } customTop)
        {
            // Clamp against the screen the saved point lives on, not
            // always primary — otherwise dragging the overlay to a
            // secondary monitor would snap back onto primary the moment
            // the debounced save fires (SettingsChanged → PositionOverlay).
            // Fall back to primary only when the saved point is off every
            // screen (e.g., a monitor was unplugged) — that rescues the
            // overlay onto the visible screen without overwriting the
            // saved coords, so re-plugging the monitor restores it.
            var customPoint = new PixelPoint(
                (int)Math.Round(customLeft),
                (int)Math.Round(customTop));
            var targetScreen = Screens?.ScreenFromPoint(customPoint) ?? primaryScreen;
            var targetWorkArea = targetScreen.WorkingArea;

            var (clampedLeft, clampedTop) = AppSettings.ClampOverlayPositionToWorkArea(
                customLeft,
                customTop,
                targetWorkArea.X,
                targetWorkArea.Y,
                targetWorkArea.Right,
                targetWorkArea.Bottom,
                width,
                height);
            SetPositionProgrammatically(
                new PixelPoint(
                    (int)Math.Round(clampedLeft),
                    (int)Math.Round(clampedTop)));
            return;
        }

        var workArea = primaryScreen.WorkingArea;
        var x = workArea.X + (workArea.Width - (int)Math.Ceiling(width)) / 2;
        var y =
            _settings.Current.OverlayPosition == OverlayPosition.Top
                ? workArea.Y + 12
                : workArea.Bottom - (int)Math.Ceiling(height) - 12;

        SetPositionProgrammatically(new PixelPoint(x, y));
    }

    private void SetPositionProgrammatically(PixelPoint point)
    {
        _programmaticPositionChange = true;
        try
        {
            Position = point;
        }
        finally
        {
            _programmaticPositionChange = false;
        }
    }

    private void OnUserPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _userDragging = true;
        BeginMoveDrag(e);
    }

    private void OnUserPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _userDragging = false;
    }

    private void OnUserPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _userDragging = false;
    }

    private void OnUserPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_programmaticPositionChange || _settings is null)
        {
            return;
        }

        if (!_userDragging)
        {
            return;
        }

        _pendingDragPosition = e.Point;

        _dragSaveTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnDragSaveTimerTick);
        _dragSaveTimer.Stop();
        _dragSaveTimer.Start();
    }

    private void OnDragSaveTimerTick(object? sender, EventArgs e)
    {
        _dragSaveTimer?.Stop();

        if (_settings is null || _pendingDragPosition is null)
        {
            return;
        }

        var pos = _pendingDragPosition.Value;
        _pendingDragPosition = null;

        _settings.Save(_settings.Current with
        {
            OverlayCustomLeft = (double)pos.X,
            OverlayCustomTop = (double)pos.Y,
        });
    }
}
