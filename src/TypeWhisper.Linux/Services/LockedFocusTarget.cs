using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     A null record means focus locking is off. A record with an <see cref="Element" /> locks that field.
///     Without one, <see cref="WindowOnly" /> means no field was found, so the window the dictation started
///     in is the target; otherwise locking was refused and insertion must fall back.
/// </summary>
public sealed record LockedFocusTarget(AtSpiElementRef? Element, bool WindowOnly = false)
{
    public static LockedFocusTarget Refused { get; } = new(Element: null);

    public static LockedFocusTarget Window { get; } = new(Element: null, WindowOnly: true);

    /// <summary>For a window-only lock, confirms right before input that the recording's window still has focus.</summary>
    public Func<Task<bool>>? IsWindowStillFocused { get; init; }
}
