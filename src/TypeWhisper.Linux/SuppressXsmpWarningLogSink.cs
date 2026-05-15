using Avalonia.Logging;

namespace TypeWhisper.Linux;

/// <summary>
/// <see cref="ILogSink"/> decorator that drops one specific, harmless
/// Avalonia warning and passes everything else through unchanged.
///
/// On startup Avalonia's X11 backend (<c>X11PlatformLifetimeEvents</c>)
/// calls <c>SmcOpenConnection</c> to register with an X11 session
/// manager. We run on the X11 backend under Wayland, where there is no
/// X11 session manager, so libSM fails immediately and Avalonia logs
/// <c>"SMLib/ICELib reported a new error: SESSION_MANAGER environment
/// variable not defined"</c> at <see cref="LogEventLevel.Warning"/>.
///
/// The failure is expected and carries no actionable information — do
/// NOT try to "fix" the XSMP connection. Setting <c>SESSION_MANAGER</c>
/// in the environment does not help: an empty value just makes libSM
/// fail with a different message. Filtering the log line is the only
/// reliable suppression.
/// </summary>
internal sealed class SuppressXsmpWarningLogSink : ILogSink
{
    // The exact, fully-interpolated message Avalonia's X11 backend emits
    // when there is no X11 session manager. Matching the whole string —
    // not just the "SMLib/ICELib reported a new error" prefix — keeps any
    // other SMLib/ICELib failure (a different, genuinely actionable error)
    // flowing through to the trace.
    private const string XsmpWarningMessage =
        "SMLib/ICELib reported a new error: SESSION_MANAGER environment variable not defined";

    private readonly ILogSink _inner;

    public SuppressXsmpWarningLogSink(ILogSink inner) => _inner = inner;

    public bool IsEnabled(LogEventLevel level, string area) => _inner.IsEnabled(level, area);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsXsmpWarning(level, messageTemplate)) return;
        _inner.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsXsmpWarning(level, messageTemplate)) return;
        _inner.Log(level, area, source, messageTemplate, propertyValues);
    }

    // Suppress only the known harmless warning: it is logged at Warning
    // level as a fully-interpolated string with no {n} placeholders, so an
    // exact, level-gated match is stable. Anything logged at a higher
    // severity, or with different text, is left alone.
    private static bool IsXsmpWarning(LogEventLevel level, string messageTemplate) =>
        level == LogEventLevel.Warning
        && string.Equals(messageTemplate, XsmpWarningMessage, StringComparison.Ordinal);
}
