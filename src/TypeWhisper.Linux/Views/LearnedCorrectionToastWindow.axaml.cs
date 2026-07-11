using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Views;

/// <summary>
///     Dedicated toast window for the learned-corrections "Learned 'X' → 'Y'" feedback on
///     desktop environments. Larger than the old overlay feedback band and placed beside the
///     corrected element (via <see cref="LearnedToastPlacement" /> and its AT-SPI screen box),
///     so it appears where the user is actually looking — even on another monitor.
///     <para>
///         Must not steal focus from the app the user is typing in: it is created with
///         ShowActivated=false and only ever shown via <see cref="Window.Show()" /> (never
///         <see cref="Window.Activate" />d or focused). Mirrors DictationOverlayWindow's
///         utility-window style (Topmost, no decorations, no taskbar entry).
///     </para>
/// </summary>
public partial class LearnedCorrectionToastWindow : Window
{
    private Action? _onUndo;

    // Extents of the correction currently being shown, captured so a Reposition posted after
    // ShowToast (once layout has produced a real size) anchors to the right element.
    private AtSpiScreenRect? _pendingExtents;

    public LearnedCorrectionToastWindow()
    {
        InitializeComponent();

        // Belt-and-suspenders alongside the XAML: keep this a passive utility surface that never
        // takes focus from the target app (ShowActivated=false is the load-bearing part).
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;

        Title = "TypeWhisper Learned Correction";

        UndoButton.Click += (_, _) => _onUndo?.Invoke();
    }

    /// <summary>
    ///     Shows or updates the toast: sets the message and Undo affordance, positions the window
    ///     beside <paramref name="sourceExtents" /> (or bottom-center as a fallback), then reveals
    ///     it without activating. Re-invoking updates the existing window in place.
    /// </summary>
    public void ShowToast(
        string message,
        bool showUndo,
        string undoLabel,
        AtSpiScreenRect? sourceExtents,
        Action onUndo
    )
    {
        _onUndo = onUndo;
        _pendingExtents = sourceExtents;
        MessageText.Text = message;
        UndoButton.Content = undoLabel;
        UndoButton.IsVisible = showUndo;

        // WORKAROUND (backlog #16): map once and drive visibility via Opacity, never Hide()/Show()
        // — Show() after Hide() is unreliable on GNOME Mutter for utility windows (ShowActivated=
        // False / Topmost / ShowInTaskbar=False), exactly this window's profile. ShowActivated=false
        // keeps focus with the target app.
        if (!IsVisible)
        {
            Show();
            // Mapped once and kept alive via Opacity (never Hide()), so without this the WM pins it
            // to the workspace it was first shown on and later feedback/Undo stays invisible after
            // the user switches desktops. Mirrors DictationOverlayWindow's sticky handling.
            MakeStickyAcrossWorkspaces();
        }

        Opacity = 1.0;
        IsHitTestVisible = true;

        // Position after the content is laid out so DesiredSize reflects the final toast size.
        // Loaded priority runs after measure/arrange for the just-set text.
        Dispatcher.UIThread.Post(Reposition, DispatcherPriority.Loaded);
    }

    public void HideToast()
    {
        // Kept mapped (see the Mutter workaround in ShowToast). Opacity 0 hides it, but neither
        // Opacity nor Avalonia's IsHitTestVisible clears the native X11 input region, so the mapped
        // Topmost toplevel keeps swallowing pointer events over its rectangle — a dead-click zone by
        // the field. Park it past every monitor's right edge so those clicks land off-screen; a WM
        // that clamps it back on-screen leaves us no worse off than Opacity 0 alone.
        Opacity = 0.0;
        IsHitTestVisible = false;
        MoveOffScreen();
    }

    // Moves the (still mapped) window just beyond the right edge of all monitors. Best-effort: on
    // Wayland client positioning is a no-op, which is fine — there the toast placement is already
    // compositor-controlled and this simply does nothing.
    private void MoveOffScreen()
    {
        var screens = CollectScreenBounds();
        if (screens.Count == 0)
        {
            return;
        }

        // Left edge at the union's right boundary puts the whole window off every screen.
        var right = screens.Max(bounds => bounds.Right);
        Position = new PixelPoint(right + 1, screens[0].Y);
    }

    private void Reposition()
    {
        if (!IsVisible)
        {
            return;
        }

        // Prefer the realized bounds; fall back to the measured desired size before the first
        // layout pass has produced non-zero bounds. Both are Avalonia device-independent units.
        var width = Bounds.Width > 0 ? Bounds.Width : Root.DesiredSize.Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Root.DesiredSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Screen bounds, PixelSize and Window.Position are all device pixels, so convert the DIP
        // size by RenderScaling first — otherwise at >100% scaling the toast is measured smaller
        // than it renders, mis-centering it and letting it clip past screen edges.
        var scaling = RenderScaling;
        var toastSize = new PixelSize(
            (int)Math.Ceiling(width * scaling),
            (int)Math.Ceiling(height * scaling)
        );
        var screens = CollectScreenBounds();
        Position = LearnedToastPlacement.Compute(_pendingExtents, screens, toastSize);
    }

    // Asks the WM to show the (mapped-once) toast on the active virtual desktop rather than
    // pinning it to the one it first appeared on. Posted at Loaded priority so the X11 toplevel is
    // mapped before the _NET_WM_STATE request (an unmapped window's request is ignored). Best-effort
    // and a no-op on pure Wayland. Mirrors DictationOverlayWindow.MakeStickyAcrossWorkspaces.
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
            DispatcherPriority.Loaded
        );
    }

    private List<PixelRect> CollectScreenBounds()
    {
        var result = new List<PixelRect>();

        var screens = Screens;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- Avalonia annotates Screens non-null, but it can be null before the platform window is realized; the placement helper handles an empty list, so guard defensively (mirrors DictationOverlayWindow).
        if (screens is null)
        {
            return result;
        }

        // Put the primary first so the fallback path (index 0) uses it.
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
}
