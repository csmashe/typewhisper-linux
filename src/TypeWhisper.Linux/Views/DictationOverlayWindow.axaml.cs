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
    private readonly DictationOverlayPlacementState _placementState = new();
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
        switch (e.PropertyName)
        {
            case nameof(DictationOverlayViewModel.HasVisibleContent):
                Dispatcher.UIThread.Post(UpdateWindowVisibility);
                break;
            case nameof(DictationOverlayViewModel.PartialText):
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
                break;
            }
            case nameof(DictationOverlayViewModel.LlmResponseText):
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
                break;
            }
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
        if (s_usesNotificationIndicator)
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
        // invisible until restart. Inner Border bindings still control which content is drawn.
        var hasContent = _viewModel.HasVisibleContent;

        if (!IsVisible)
        {
            // Keep the first mapping transparent too: OnOverlayOpened parks it before
            // _placementState.Show() runs, so a content-bearing first show is only revealed
            // by the Loaded-priority reposition below.
            Opacity = 0.0;
            IsHitTestVisible = false;
            Show();
            MakeStickyAcrossWorkspaces();
        }

        if (hasContent)
        {
            _placementState.Show();

            // Post at Loaded so a size-changing transition uses final dimensions; staying
            // transparent at the parked position until this runs avoids a visible jump.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_placementState.IsShown)
                    {
                        return;
                    }

                    PositionOverlay();
                    if (!_placementState.IsShown)
                    {
                        return;
                    }

                    Opacity = 1.0;
                    IsHitTestVisible = true;
                },
                DispatcherPriority.Loaded
            );
            return;
        }

        // Opacity and Avalonia's IsHitTestVisible do not clear a mapped toplevel's native X11
        // input region. Leaving this Topmost window at its visible coordinates would therefore
        // create a transparent dead-click rectangle on X11/XWayland. Keep it mapped for the
        // Mutter workaround, but park it beyond every monitor while hidden, like the correction
        // toast. Wayland may ignore client positioning, where this remains a harmless best effort.
        Opacity = 0.0;
        IsHitTestVisible = false;
        SetPositionProgrammatically(
            _placementState.Hide(CollectScreenBounds(), Position)
        );
    }

    // Cached — the desktop environment can't change within a session.
    private static readonly bool s_usesNotificationIndicator =
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
        if (!IsVisible)
        {
            return;
        }

        var screenBounds = CollectScreenBounds();

        // IsVisible stays true for the mapped-once Mutter workaround. Consult our own content
        // state instead, so settings, screen, and size events recompute (or preserve) an offscreen
        // parked position rather than moving the transparent X11 input rectangle back on-screen.
        if (!_placementState.IsShown)
        {
            SetPositionProgrammatically(
                _placementState.Reposition(Position, screenBounds, Position)
            );
            return;
        }

        if (_settings is null)
        {
            return;
        }

        // Don't fight the user while they're dragging the overlay
        // — a SizeChanged mid-drag would otherwise yank the window.
        if (_userDragging)
        {
            return;
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Avalonia annotates Screens non-null, but it can be null before the platform window is realized (headless/early lifecycle); keep the defensive ?. consistent with the Screens?. access below.
        var primaryScreen = Screens?.Primary;
        if (primaryScreen is null)
        {
            return;
        }

        var width = Math.Max(320, Bounds.Width);
        var height = Math.Max(56, Bounds.Height);

        if (_settings.Current is { OverlayCustomLeft: { } customLeft, OverlayCustomTop: { } customTop })
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
                _placementState.Reposition(
                    new PixelPoint(
                        (int)Math.Round(clampedLeft),
                        (int)Math.Round(clampedTop)
                    ),
                    screenBounds,
                    Position
                )
            );
            return;
        }

        var workArea = primaryScreen.WorkingArea;
        var configuredPosition = DictationOverlayPlacementState.ComputeConfiguredPosition(
            _settings.Current.OverlayPosition,
            workArea,
            new PixelSize(
                (int)Math.Ceiling(width),
                (int)Math.Ceiling(height)
            )
        );

        SetPositionProgrammatically(
            _placementState.Reposition(
                configuredPosition,
                screenBounds,
                Position
            )
        );
    }

    private List<PixelRect> CollectScreenBounds()
    {
        var result = new List<PixelRect>();

        var screens = Screens;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- Avalonia annotates Screens non-null, but it can be null before the platform window is realized; the placement state tolerates an empty list (mirrors LearnedCorrectionToastWindow).
        if (screens is null)
        {
            return result;
        }

        // Put the primary first so parking uses a stable Y coordinate, matching the correction
        // toast's documented native-X11 workaround.
        if (screens.Primary is { } primary)
        {
            result.Add(primary.Bounds);
        }

        result.AddRange(
            screens.All
                .Where(screen => !ReferenceEquals(screen, screens.Primary))
                .Select(screen => screen.Bounds)
        );

        return result;
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
        EndUserDrag();
    }

    private void OnUserPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndUserDrag();
    }

    private void EndUserDrag()
    {
        _userDragging = false;

        // A move-drag that outlived a hide (content cleared mid-drag) leaves the still-mapped
        // window on-screen wherever the WM's interactive move dropped it — that grab overrides
        // our one-off park while active. Its native X11 input region stays live regardless of
        // IsHitTestVisible, so re-park now rather than waiting for a later screen/settings/size event.
        if (!_placementState.IsShown)
        {
            PositionOverlay();
        }
    }

    private void OnUserPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_programmaticPositionChange || _settings is null)
        {
            return;
        }

        // A hidden overlay is only ever moved programmatically (parked off-screen). On X11 the
        // move's PositionChanged arrives asynchronously — after SetPositionProgrammatically has
        // cleared _programmaticPositionChange — so if content clears mid-drag the parked sentinel
        // could be mistaken for a user drag and persisted as the saved position. Never persist a
        // position while parked.
        if (!_placementState.IsShown)
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

/// <summary>
///     Deterministic visibility and placement decisions for the mapped-once dictation overlay,
///     kept independent of Window/Screens so it can be unit tested without a live compositor.
/// </summary>
internal sealed class DictationOverlayPlacementState
{
    private const int ScreenEdgeInset = 12;

    public bool IsShown { get; private set; }

    public void Show()
    {
        IsShown = true;
    }

    public PixelPoint Hide(
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        IsShown = false;
        return ComputeParkedPosition(screenBounds, currentPosition);
    }

    public PixelPoint Reposition(
        PixelPoint configuredPosition,
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        return IsShown
            ? configuredPosition
            : ComputeParkedPosition(screenBounds, currentPosition);
    }

    public static PixelPoint ComputeConfiguredPosition(
        OverlayPosition overlayPosition,
        PixelRect workArea,
        PixelSize overlaySize
    )
    {
        var x = workArea.X + (workArea.Width - overlaySize.Width) / 2;
        var y = overlayPosition == OverlayPosition.Top
            ? workArea.Y + ScreenEdgeInset
            : workArea.Bottom - overlaySize.Height - ScreenEdgeInset;

        return new PixelPoint(x, y);
    }

    private static PixelPoint ComputeParkedPosition(
        IReadOnlyList<PixelRect> screenBounds,
        PixelPoint currentPosition
    )
    {
        if (screenBounds.Count == 0)
        {
            return currentPosition;
        }

        // Match LearnedCorrectionToastWindow: the left edge just beyond the union's right boundary
        // puts the entire mapped window outside every monitor, including negative-origin layouts.
        var right = screenBounds.Max(bounds => bounds.Right);
        return new PixelPoint(right + 1, screenBounds[0].Y);
    }
}
