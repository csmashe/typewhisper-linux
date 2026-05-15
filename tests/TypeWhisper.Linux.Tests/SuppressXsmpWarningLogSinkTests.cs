using Avalonia.Logging;
using TypeWhisper.Linux;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Tests for the log-sink decorator that suppresses Avalonia's harmless
/// startup XSMP warning ("SMLib/ICELib reported a new error: SESSION_MANAGER
/// environment variable not defined") while passing every other log through.
/// </summary>
public sealed class SuppressXsmpWarningLogSinkTests
{
    private const string XsmpWarning =
        "SMLib/ICELib reported a new error: SESSION_MANAGER environment variable not defined";

    /// <summary>Inner sink that records the message templates it receives.</summary>
    private sealed class RecordingSink : ILogSink
    {
        public List<string> Messages { get; } = new();
        public bool IsEnabledResult { get; set; } = true;

        public bool IsEnabled(LogEventLevel level, string area) => IsEnabledResult;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Messages.Add(messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Messages.Add(messageTemplate);
    }

    [Fact]
    public void Log_XsmpWarning_IsDropped()
    {
        var inner = new RecordingSink();
        var sink = new SuppressXsmpWarningLogSink(inner);

        sink.Log(LogEventLevel.Warning, "X11Platform", null, XsmpWarning);

        Assert.Empty(inner.Messages);
    }

    [Fact]
    public void Log_XsmpWarning_WithPropertyValues_IsDropped()
    {
        var inner = new RecordingSink();
        var sink = new SuppressXsmpWarningLogSink(inner);

        sink.Log(LogEventLevel.Warning, "X11Platform", null, XsmpWarning, "extra");

        Assert.Empty(inner.Messages);
    }

    [Fact]
    public void Log_UnrelatedWarning_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressXsmpWarningLogSink(inner);

        sink.Log(LogEventLevel.Warning, "X11Platform", null, "Some other warning");
        sink.Log(LogEventLevel.Error, "Layout", null, "Measure failed for {0}", "Button");

        Assert.Equal(new[] { "Some other warning", "Measure failed for {0}" }, inner.Messages);
    }

    [Fact]
    public void Log_XsmpMessageAtErrorLevel_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressXsmpWarningLogSink(inner);

        // Same text, higher severity — a genuinely actionable failure must
        // not be swallowed just because it shares the warning's wording.
        sink.Log(LogEventLevel.Error, "X11Platform", null, XsmpWarning);

        Assert.Equal(new[] { XsmpWarning }, inner.Messages);
    }

    [Fact]
    public void Log_OtherSmlibErrorWithSamePrefix_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressXsmpWarningLogSink(inner);

        // A different SMLib/ICELib error sharing the prefix must still be
        // visible — the filter matches the whole message, not the prefix.
        const string otherError = "SMLib/ICELib reported a new error: connection refused";
        sink.Log(LogEventLevel.Warning, "X11Platform", null, otherError);

        Assert.Equal(new[] { otherError }, inner.Messages);
    }

    [Fact]
    public void IsEnabled_DelegatesToInner()
    {
        var inner = new RecordingSink { IsEnabledResult = false };
        var sink = new SuppressXsmpWarningLogSink(inner);

        Assert.False(sink.IsEnabled(LogEventLevel.Warning, "X11Platform"));

        inner.IsEnabledResult = true;
        Assert.True(sink.IsEnabled(LogEventLevel.Warning, "X11Platform"));
    }
}
