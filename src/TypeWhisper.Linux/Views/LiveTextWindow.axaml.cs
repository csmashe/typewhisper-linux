using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

/// <summary>
///     Floating live text: dragged by its header, resized from its edges, with position and size
///     saved and fitted to the current screen's work area. Shares DictationOverlayWindow's
///     map-once, park-offscreen visibility so it behaves the same on Mutter and X11.
/// </summary>
public partial class LiveTextWindow : Window
{
    private const int EdgeThickness = 6;
    private const int CornerSize = 14;

    // Suppressed on tiling WMs like the overlay: a toplevel there takes a tile and focus.
    private static readonly bool s_usesNotificationIndicator =
        DesktopDetector.UsesNotificationRecordingIndicator();

    private readonly DictationOverlayPlacementState _placementState = new();
    private readonly ISettingsService? _settings;
    private readonly LiveTextWindowViewModel? _viewModel;
    private bool _applyingBounds;
    private Interaction _interaction;
    private DispatcherTimer? _saveTimer;
    private bool _sizeChangedByUser;
    private (double? Left, double? Top, int Width, int Height) _ownBounds;

    public LiveTextWindow()
    {
        InitializeComponent();
    }

    public LiveTextWindow(LiveTextWindowViewModel viewModel, ISettingsService settings)
        : this()
    {
        DataContext = _viewModel = viewModel;
        _settings = settings;
        Title = "TypeWhisper Live Text";

        AddResizeHandles();
        Loc.Instance.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(UpdateResizeTips);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(OnSettingsChanged);

        Opened += (_, _) =>
        {
            ApplyBounds();
            if (Screens is { } screens)
            {
                screens.Changed += OnScreensChanged;
            }
        };
        // A WM close (Alt+F4) would end this singleton for the session; dismiss the preview instead.
        Closing += (_, e) =>
        {
            if (e is not { CloseReason: WindowCloseReason.WindowClosing, IsProgrammatic: false })
            {
                return;
            }

            e.Cancel = true;
            _viewModel.Dismiss();
        };
        Closed += (_, _) =>
        {
            if (Screens is { } screens)
            {
                screens.Changed -= OnScreensChanged;
            }
        };

        DragHandle.PointerPressed += OnDragHandlePressed;
        PointerReleased += (_, _) => EndInteraction();
        PointerCaptureLost += (_, _) => EndInteraction();
        // The WM's move/resize grab can swallow the release; the next buttonless motion ends it.
        PointerMoved += (_, e) =>
        {
            if (_interaction != Interaction.None && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                EndInteraction();
            }
        };
        PositionChanged += (_, _) => OnUserBoundsChanged();
        SizeChanged += (_, _) => OnUserBoundsChanged();
    }

    private enum Interaction
    {
        None,
        Move,
        Resize,
    }

    public void Initialize()
    {
        UpdateWindowVisibility();
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _interaction = Interaction.None;
            ApplyBounds();
        });
    }

    // A WM grab can swallow the release with the pointer outside this window, leaving a drag that
    // never ends. Bounds changed elsewhere (Reset, the size fields) end it, so they always apply.
    private void OnSettingsChanged()
    {
        if (_interaction != Interaction.None && SavedBounds() != _ownBounds)
        {
            // The external edit wins over the drag's unsaved geometry.
            _saveTimer?.Stop();
            _sizeChangedByUser = false;
            _interaction = Interaction.None;
        }

        ApplyBounds();
    }

    private (double? Left, double? Top, int Width, int Height) SavedBounds()
    {
        var current = _settings!.Current;
        return (current.LiveTextWindowLeft, current.LiveTextWindowTop, current.LiveTextWindowWidth, current.LiveTextWindowHeight);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LiveTextWindowViewModel.HasVisibleContent):
                Dispatcher.UIThread.Post(UpdateWindowVisibility);
                break;
            case nameof(LiveTextWindowViewModel.Text):
                // Background priority so the scroll runs after layout measures the new text.
                Dispatcher.UIThread.Post(() => TextScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
                break;
        }
    }

    private void UpdateWindowVisibility()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (s_usesNotificationIndicator)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Opacity = 0.0;
            IsHitTestVisible = false;
            Show();
            MakeStickyAcrossWorkspaces();
        }

        if (_viewModel.HasVisibleContent)
        {
            // A drag whose release was swallowed would keep ApplyBounds from bringing the window back.
            if (!_placementState.IsShown)
            {
                _interaction = Interaction.None;
            }

            _placementState.Show();
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_placementState.IsShown)
                    {
                        return;
                    }

                    ApplyBounds();
                    SetClickThrough(false);
                    Opacity = 1.0;
                    IsHitTestVisible = true;
                },
                DispatcherPriority.Loaded);
            return;
        }

        // A transparent mapped window still takes clicks on X11, and Mutter clamps the off-screen
        // park back onto a monitor, so empty its input region too. A drag in progress stays
        // tracked: when it ends, EndInteraction re-parks the window.
        FlushPendingSave();
        Opacity = 0.0;
        IsHitTestVisible = false;
        SetClickThrough(true);
        SetBounds(new PixelRect(_placementState.Hide(CollectScreenBounds(), Position), PixelSize.Empty));
    }

    private void MakeStickyAcrossWorkspaces()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (TryGetPlatformHandle() is { Handle: var xid } && xid != IntPtr.Zero)
                {
                    X11StickyWindow.MakeSticky((nuint)xid);
                }
            },
            DispatcherPriority.Loaded);
    }

    private void SetClickThrough(bool passThrough)
    {
        if (TryGetPlatformHandle() is { Handle: var xid } && xid != IntPtr.Zero)
        {
            X11InputPassthrough.Set((nuint)xid, passThrough);
        }
    }

    private void ApplyBounds()
    {
        if (!IsVisible || _settings is null || _interaction != Interaction.None)
        {
            return;
        }

        // Restoring the saved bounds before a just-finished move or resize is saved would lose it.
        FlushPendingSave();

        if (!_placementState.IsShown)
        {
            SetBounds(new PixelRect(
                _placementState.Reposition(Position, CollectScreenBounds(), Position),
                PixelSize.Empty));
            return;
        }

        // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Screens can be null before the platform window is realized (see DictationOverlayWindow).
        if (Screens?.Primary is not { } primary)
        {
            return;
        }

        var settings = _settings.Current;
        PixelPoint? saved = settings is { LiveTextWindowLeft: { } left, LiveTextWindowTop: { } top }
            ? new PixelPoint((int)Math.Round(left), (int)Math.Round(top))
            : null;

        // Fit to the screen holding most of the saved window, so an edge hanging past a monitor
        // doesn't move it; primary when it's on no screen (unplugged).
        var screen = primary;
        if (saved is { } point)
        {
            var all = Screens.All.ToList();
            var index = LiveTextWindowPlacement.IndexOfMostOverlapped(
                all.Select(candidate => (candidate.Bounds, candidate.Scaling)).ToList(),
                point,
                settings.LiveTextWindowWidth,
                settings.LiveTextWindowHeight);
            if (index >= 0)
            {
                screen = all[index];
            }
        }

        var size = LiveTextWindowPlacement.ToPixels(
            settings.LiveTextWindowWidth,
            settings.LiveTextWindowHeight,
            screen.Scaling);
        var position = saved
                       ?? LiveTextWindowPlacement.DefaultPosition(screen.WorkingArea, size, settings.OverlayPosition);
        SetBounds(LiveTextWindowPlacement.Fit(position, size, screen.WorkingArea), screen.Scaling);
    }

    // An empty size moves the window without resizing it.
    private void SetBounds(PixelRect bounds, double scaling = 1)
    {
        _applyingBounds = true;
        try
        {
            if (bounds is { Width: > 0, Height: > 0 })
            {
                Width = bounds.Width / scaling;
                Height = bounds.Height / scaling;
            }

            Position = bounds.Position;
        }
        finally
        {
            _applyingBounds = false;
        }
    }

    private List<PixelRect> CollectScreenBounds()
    {
        var result = new List<PixelRect>();
        var screens = Screens;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- Screens can be null before the platform window is realized (see DictationOverlayWindow).
        if (screens is null)
        {
            return result;
        }

        if (screens.Primary is { } primary)
        {
            result.Add(primary.Bounds);
        }

        result.AddRange(
            screens.All
                .Where(screen => !ReferenceEquals(screen, screens.Primary))
                .Select(screen => screen.Bounds));
        return result;
    }

    private void AddResizeHandles()
    {
        AddResizeHandle(WindowEdge.West, HorizontalAlignment.Left, VerticalAlignment.Stretch, EdgeThickness, double.NaN, StandardCursorType.SizeWestEast);
        AddResizeHandle(WindowEdge.East, HorizontalAlignment.Right, VerticalAlignment.Stretch, EdgeThickness, double.NaN, StandardCursorType.SizeWestEast);
        AddResizeHandle(WindowEdge.North, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, EdgeThickness, StandardCursorType.SizeNorthSouth);
        AddResizeHandle(WindowEdge.South, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, EdgeThickness, StandardCursorType.SizeNorthSouth);
        AddResizeHandle(WindowEdge.NorthWest, HorizontalAlignment.Left, VerticalAlignment.Top, CornerSize, CornerSize, StandardCursorType.TopLeftCorner);
        AddResizeHandle(WindowEdge.NorthEast, HorizontalAlignment.Right, VerticalAlignment.Top, CornerSize, CornerSize, StandardCursorType.TopRightCorner);
        AddResizeHandle(WindowEdge.SouthWest, HorizontalAlignment.Left, VerticalAlignment.Bottom, CornerSize, CornerSize, StandardCursorType.BottomLeftCorner);
        AddResizeHandle(WindowEdge.SouthEast, HorizontalAlignment.Right, VerticalAlignment.Bottom, CornerSize, CornerSize, StandardCursorType.BottomRightCorner);
    }

    private void AddResizeHandle(
        WindowEdge edge,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        double width,
        double height,
        StandardCursorType cursor)
    {
        var handle = new Border
        {
            Background = Brushes.Transparent,
            Width = width,
            Height = height,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Cursor = new Cursor(cursor),
        };
        ToolTip.SetTip(handle, Loc.Instance["LiveText.ResizeHint"]);
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            BeginInteraction(Interaction.Resize);
            BeginResizeDrag(edge, e);
            e.Handled = true;
        };
        ResizeHandles.Children.Add(handle);
    }

    private void UpdateResizeTips()
    {
        foreach (var handle in ResizeHandles.Children)
        {
            ToolTip.SetTip(handle, Loc.Instance["LiveText.ResizeHint"]);
        }
    }

    private void OnDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginInteraction(Interaction.Move);
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void BeginInteraction(Interaction interaction)
    {
        _interaction = interaction;
        if (_settings is not null)
        {
            _ownBounds = SavedBounds();
        }
    }

    private void EndInteraction()
    {
        if (_interaction == Interaction.None)
        {
            return;
        }

        _interaction = Interaction.None;

        // Re-fit to the saved bounds (ApplyBounds saves the pending change first): settings
        // changes skipped mid-drag are replayed, and a drag that outlived a hide is re-parked.
        ApplyBounds();
    }

    private void OnUserBoundsChanged()
    {
        // Programmatic and parked moves are never saved; on X11 their events can arrive after
        // _applyingBounds is cleared, so only a user-started move or resize persists anything.
        if (_applyingBounds || _settings is null || !_placementState.IsShown || _interaction == Interaction.None)
        {
            return;
        }

        // Only a resize saves the size, so a move never stores a size Fit shrank for a smaller screen.
        _sizeChangedByUser |= _interaction == Interaction.Resize;
        _saveTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            (_, _) => SaveBounds());
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        if (_saveTimer?.IsEnabled == true)
        {
            SaveBounds();
        }
    }

    private void SaveBounds()
    {
        _saveTimer?.Stop();
        if (_settings is null || !_placementState.IsShown)
        {
            return;
        }

        var position = Position;
        var (width, height) = LiveTextWindowPlacement.ToSavedSize(ClientSize);
        var saveSize = _sizeChangedByUser;
        _sizeChangedByUser = false;
        _settings.Update(current => current with
        {
            LiveTextWindowLeft = position.X,
            LiveTextWindowTop = position.Y,
            LiveTextWindowWidth = saveSize ? width : current.LiveTextWindowWidth,
            LiveTextWindowHeight = saveSize ? height : current.LiveTextWindowHeight,
        });
        _ownBounds = SavedBounds();
    }
}
