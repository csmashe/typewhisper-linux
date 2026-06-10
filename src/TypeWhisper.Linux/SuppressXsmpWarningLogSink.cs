using Avalonia.Logging;

namespace TypeWhisper.Linux;

/// <summary>
///     <see cref="ILogSink" /> decorator that drops one known-harmless Avalonia warning.
///     When running the X11 backend under Wayland there is no X11 session manager, so
///     libSM fails and logs "SMLib/ICELib reported a new error: SESSION_MANAGER environment
///     variable not defined". This is expected and not fixable by setting SESSION_MANAGER —
///     filtering the specific log line is the only reliable suppression.
/// </summary>
internal sealed class SuppressXsmpWarningLogSink : ILogSink
{
    // Match the full string, not just the prefix, so any other SMLib/ICELib
    // error (which may be actionable) still flows through to the trace.
    private const string XsmpWarningMessage =
        "SMLib/ICELib reported a new error: SESSION_MANAGER environment variable not defined";

    private readonly ILogSink _inner;

    public SuppressXsmpWarningLogSink(ILogSink inner)
    {
        _inner = inner;
    }

    public bool IsEnabled(LogEventLevel level, string area)
    {
        return _inner.IsEnabled(level, area);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsXsmpWarning(level, messageTemplate))
        {
            return;
        }

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
        if (IsXsmpWarning(level, messageTemplate))
        {
            return;
        }

        _inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    // The warning is fully interpolated (no {n} placeholders), so an exact
    // level-gated string match is stable and won't catch unrelated errors.
    private static bool IsXsmpWarning(LogEventLevel level, string messageTemplate)
    {
        return level == LogEventLevel.Warning
               && string.Equals(messageTemplate, XsmpWarningMessage, StringComparison.Ordinal);
    }
}