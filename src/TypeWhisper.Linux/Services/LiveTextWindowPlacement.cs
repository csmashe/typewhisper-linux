using Avalonia;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Bounds math for the floating live-text window, kept off Window/Screens for unit tests.
///     Positions are physical pixels; saved sizes are device-independent pixels, scaled per screen.
/// </summary>
internal static class LiveTextWindowPlacement
{
    private const int ScreenEdgeInset = 12;

    // Matches the overlay's minimum footprint so the default spot clears it.
    private const int OverlayHeightAllowance = 56;

    /// <summary>
    ///     Shrinks the window to fit the work area, then clamps its position inside it, so a saved
    ///     placement from a larger or since-removed monitor never ends up off-screen.
    /// </summary>
    public static PixelRect Fit(PixelPoint position, PixelSize size, PixelRect workArea)
    {
        var width = Math.Clamp(size.Width, 1, Math.Max(1, workArea.Width));
        var height = Math.Clamp(size.Height, 1, Math.Max(1, workArea.Height));
        var x = Math.Clamp(position.X, workArea.X, workArea.Right - width);
        var y = Math.Clamp(position.Y, workArea.Y, workArea.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    ///     Index of the screen holding the largest share of the window, or -1 when it's on none. The
    ///     window is sized with each screen's own scaling, as it would be if placed there, so shares
    ///     (not pixel areas) compare fairly across mixed-DPI screens.
    /// </summary>
    public static int IndexOfMostOverlapped(
        IReadOnlyList<(PixelRect Bounds, double Scaling)> screens,
        PixelPoint position,
        int width,
        int height)
    {
        var best = -1;
        double bestShare = 0;
        for (var i = 0; i < screens.Count; i++)
        {
            var window = new PixelRect(position, ToPixels(width, height, screens[i].Scaling));
            var overlap = screens[i].Bounds.Intersect(window);
            var share = (double)overlap.Width * overlap.Height / ((double)window.Width * window.Height);
            if (share <= bestShare)
            {
                continue;
            }

            best = i;
            bestShare = share;
        }

        return best;
    }

    /// <summary>Centered, on the far side of the overlay's default spot from the screen edge.</summary>
    public static PixelPoint DefaultPosition(PixelRect workArea, PixelSize size, OverlayPosition overlayPosition)
    {
        var x = workArea.X + (workArea.Width - size.Width) / 2;
        const int offset = ScreenEdgeInset + OverlayHeightAllowance + ScreenEdgeInset;
        var y = overlayPosition == OverlayPosition.Top
            ? workArea.Y + offset
            : workArea.Bottom - offset - size.Height;
        return new PixelPoint(x, y);
    }

    public static PixelSize ToPixels(int width, int height, double scaling)
    {
        var scale = ValidScaling(scaling);
        return new PixelSize(
            (int)Math.Round(AppSettings.NormalizeLiveTextWindowWidth(width) * scale),
            (int)Math.Round(AppSettings.NormalizeLiveTextWindowHeight(height) * scale));
    }

    // ClientSize is already in device-independent pixels.
    public static (int Width, int Height) ToSavedSize(Size clientSize)
    {
        return (
            AppSettings.NormalizeLiveTextWindowWidth((int)Math.Round(clientSize.Width)),
            AppSettings.NormalizeLiveTextWindowHeight((int)Math.Round(clientSize.Height)));
    }

    private static double ValidScaling(double scaling) =>
        double.IsFinite(scaling) && scaling > 0 ? scaling : 1;
}
