using Avalonia;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Pure geometry for placing the learned-corrections toast. Given the corrected element's
///     on-screen box (from AT-SPI), the available screens, and the toast size, it returns the
///     top-left corner at which to show the toast. Kept static and side-effect-free so the
///     placement rules are unit-testable without an Avalonia window or lifetime.
/// </summary>
public static class LearnedToastPlacement
{
    // Vertical gap between the element and the toast so they don't visually touch.
    private const int Gap = 12;

    // Inset from the primary screen's bottom edge used by the no-extents fallback — high enough
    // to clear typical taskbars/docks, and far more useful than the old top-strip band.
    private const int FallbackBottomInset = 80;

    /// <summary>
    ///     Computes the toast's top-left corner. When <paramref name="source" /> is a plausible
    ///     element box (positive size that intersects a screen), the toast is centered
    ///     horizontally on the element and placed just below it — flipping above if it would
    ///     overflow the bottom — then clamped fully inside the screen holding the element's
    ///     center. Otherwise it falls back to the bottom-center of the primary screen.
    /// </summary>
    public static PixelPoint Compute(
        AtSpiScreenRect? source,
        IReadOnlyList<PixelRect> screens,
        PixelSize toastSize
    )
    {
        var primary = screens.Count > 0 ? screens[0] : new PixelRect(0, 0, 1920, 1080);

        if (source is { } rect && IsPlausible(rect, screens))
        {
            var elementRect = new PixelRect(rect.X, rect.Y, rect.Width, rect.Height);

            // Clamp to the screen that holds the element's center; a box straddling monitors
            // resolves to one so the toast doesn't land in the seam between them.
            var screen = ScreenContaining(elementRect.Center, screens) ?? primary;

            var x = elementRect.X + (elementRect.Width - toastSize.Width) / 2;

            // Below the element by default; flip above only when below would clip the toast off
            // the screen's bottom edge AND above actually fits (otherwise below + clamp is best).
            var below = elementRect.Bottom + Gap;
            var above = elementRect.Y - Gap - toastSize.Height;
            var y = below + toastSize.Height <= screen.Bottom || above < screen.Y
                ? below
                : above;

            return Clamp(new PixelPoint(x, y), toastSize, screen);
        }

        // No usable extents (native-Wayland apps often report 0,0 or window-relative junk):
        // bottom-center of the primary screen.
        var fallbackX = primary.X + (primary.Width - toastSize.Width) / 2;
        var fallbackY = primary.Bottom - toastSize.Height - FallbackBottomInset;
        return Clamp(new PixelPoint(fallbackX, fallbackY), toastSize, primary);
    }

    // Extents are plausible only with positive size AND overlapping some screen — this rejects the
    // 0,0/0×0 or window-relative garbage native-Wayland apps report, which would place the toast
    // off-screen or in a corner.
    private static bool IsPlausible(AtSpiScreenRect rect, IReadOnlyList<PixelRect> screens)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var box = new PixelRect(rect.X, rect.Y, rect.Width, rect.Height);
        return screens.Any(screen => screen.Intersects(box));
    }

    private static PixelRect? ScreenContaining(PixelPoint point, IReadOnlyList<PixelRect> screens)
    {
        foreach (var screen in screens)
        {
            if (screen.Contains(point))
            {
                return screen;
            }
        }

        return null;
    }

    // Keeps the whole toast inside the target screen. If the toast is wider/taller than the
    // screen it pins to the top-left rather than pushing content off the far edge.
    private static PixelPoint Clamp(PixelPoint point, PixelSize size, PixelRect screen)
    {
        var maxX = Math.Max(screen.X, screen.Right - size.Width);
        var maxY = Math.Max(screen.Y, screen.Bottom - size.Height);
        var x = Math.Clamp(point.X, screen.X, maxX);
        var y = Math.Clamp(point.Y, screen.Y, maxY);
        return new PixelPoint(x, y);
    }
}
