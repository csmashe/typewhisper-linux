using Avalonia.Logging;

namespace TypeWhisper.Linux;

/// <summary>
///     <see cref="ILogSink" /> decorator that drops one specific, harmless
///     render-loop exception logged by Avalonia's compositor on certain
///     GLX configurations (notably NVIDIA hybrid graphics on native X11
///     and Mesa under XWayland).
///     Avalonia's GLX context-restore path occasionally calls
///     <c>Monitor.Exit</c> on a lock it does not own, throwing
///     <see cref="SynchronizationLockException" /> from
///     <c>GlxContext.RestoreContext.Dispose</c>. The throw happens AFTER
///     the frame body has rendered, the compositor catches it, and the
///     render loop continues — transparency on the dictation overlay
///     still works. The only damage is one log line per frame, which
///     drowns out everything else useful in the trace.
///     The filter is intentionally narrow: it requires the propertyValue
///     to be a <see cref="SynchronizationLockException" /> whose stack
///     trace contains the specific <c>GlxContext.RestoreContext.Dispose</c>
///     frame. Any other render-loop exception — including a different
///     SynchronizationLockException — flows through unchanged.
/// </summary>
internal sealed class SuppressGlxRenderExceptionLogSink : ILogSink
{
    // The exact stack frame that marks the harmless Avalonia GLX dispose
    // path. Matching the frame, not just the exception type, keeps any
    // other SynchronizationLockException visible.
    private const string GlxDisposeFrame =
        "Avalonia.X11.Glx.GlxContext.RestoreContext.Dispose";

    private readonly ILogSink _inner;

    public SuppressGlxRenderExceptionLogSink(ILogSink inner)
    {
        _inner = inner;
    }

    public bool IsEnabled(LogEventLevel level, string area)
    {
        return _inner.IsEnabled(level, area);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        _inner.Log(level, area, source, messageTemplate);
    }

    public void Log(
        LogEventLevel level,
        string area,
        object? source,
        string messageTemplate,
        params object?[] propertyValues
    )
    {
        if (IsHarmlessGlxDisposeException(propertyValues))
        {
            return;
        }

        _inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    private static bool IsHarmlessGlxDisposeException(object?[] propertyValues)
    {
        return propertyValues.Any(value =>
            value is SynchronizationLockException ex
            && ex.StackTrace is { } trace
            && trace.Contains(GlxDisposeFrame, StringComparison.Ordinal));
    }
}
