using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     A null record means focus locking is off. A record with a null element means
///     locking was requested but no usable field was captured, so insertion must fall back.
/// </summary>
public sealed record LockedFocusTarget(AtSpiElementRef? Element);
