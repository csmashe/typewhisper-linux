using Avalonia.Logging;

namespace TypeWhisper.Linux;

/// <summary>
///     <see cref="ILogSink" /> decorator that suppresses a harmless per-frame
///     <see cref="SynchronizationLockException" /> thrown by Avalonia's GLX
///     context-restore path on NVIDIA hybrid graphics (X11) and Mesa/XWayland.
///     The exception occurs after the frame renders; the compositor catches it
///     and continues — but without filtering it drowns the trace.
///     The filter requires the specific <c>GlxContext.RestoreContext.Dispose</c>
///     stack frame; any other <see cref="SynchronizationLockException" /> passes through.
/// </summary>
internal sealed class SuppressGlxRenderExceptionLogSink : ILogSink
{
    // Match on the specific stack frame, not just the exception type,
    // so other SynchronizationLockExceptions remain visible.
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
            value is SynchronizationLockException { StackTrace: { } trace } && trace.Contains(GlxDisposeFrame, StringComparison.Ordinal));
    }
}