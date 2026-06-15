using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
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

        Opened += OnOverlayOpened;
        Closed += OnOverlayClosed;
        SizeChanged += (_, _) => PositionOverlay();

        PointerPressed += OnUserPointerPressed;
        PointerReleased += OnUserPointerReleased;
        PointerCaptureLost += OnUserPointerCaptureLost;
        PositionChanged += OnUserPositionChanged;
    }

    private void OnOverlayOpened(object? sender, EventArgs e)
    {
        PositionOverlay();

        // Recover from display changes (monitor hotplug, resolution change, resume from
        // sleep, session unlock) — the WM can leave the overlay off-screen or on a monitor
        // that no longer exists. Re-running PositionOverlay re-clamps it to a valid work
        // area (or the saved screen if it's back). Avalonia surfaces all of these through
        // Screens.Changed; the Win32 SystemEvents equivalents upstream uses don't exist here.
        if (Screens is { } screens)
        {
            screens.Changed += OnScreensChanged;
        }
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (Screens is { } screens)
        {
            screens.Changed -= OnScreensChanged;
        }
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        // Post so Avalonia has settled the new screen geometry before we re-measure work areas.
        Dispatcher.UIThread.Post(PositionOverlay);
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
            // Background priority so the scroll runs after layout measures the updated text.
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
            // Same after-layout auto-scroll as PartialText, for the streamed LLM response area.
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

        // On tiling WMs the overlay is suppressed: an XWayland toplevel on a tiler reserves
        // a tile, steals focus, and blurs into a box — use the notification indicator instead.
        if (UsesNotificationIndicator)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        // WORKAROUND (backlog item 16): Show() once and drive visibility via Opacity instead of
        // Hide() — Avalonia's Show() after Hide() is unreliable on GNOME Mutter for utility windows
        // (ShowActivated=False / Topmost / ShowInTaskbar=False): some shows leave the window
        // invisible until restart. Fully transparent surface is free; inner Border bindings
        // still control which content is drawn.
        var hasContent = _viewModel.HasVisibleContent;

        if (!IsVisible)
        {
            Show();
            MakeStickyAcrossWorkspaces();
        }

        Opacity = hasContent ? 1.0 : 0.0;
        IsHitTestVisible = hasContent;

        if (hasContent)
        {
            Dispatcher.UIThread.Post(PositionOverlay, DispatcherPriority.Loaded);
        }
    }

    // Cached — the desktop environment can't change within a session.
    private static readonly bool UsesNotificationIndicator =
        DesktopDetector.UsesNotificationRecordingIndicator();

    // The overlay is mapped once and kept alive via Opacity (see UpdateWindowVisibility),
    // so it stays pinned to the workspace it was first mapped on. Marking it sticky lets the
    // WM show it on the active workspace instead — so the recording indicator follows the user.
    // Posted at Loaded priority so the X11 toplevel is mapped before the request is sent
    // (an unmapped window's _NET_WM_STATE ClientMessage is ignored by the WM).
    private void MakeStickyAcrossWorkspaces()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var handle = TryGetPlatformHandle();
                if (handle is { Handle: var xid } && xid != IntPtr.Zero)
                {
                    X11StickyWindow.MakeSticky((nuint)xid);
                }
            },
            DispatcherPriority.Loaded);
    }

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
            // Clamp to the screen the saved point is on, not always primary — otherwise
            // dragging to a secondary monitor would snap back on the debounced save.
            // Fall back to primary only when the point is off every screen (e.g. unplugged
            // monitor) so re-plugging restores the saved position.
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
