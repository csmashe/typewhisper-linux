using Avalonia.Logging;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Tests for the log-sink decorator that drops the harmless per-frame
///     SynchronizationLockException thrown by Avalonia's
///     <c>GlxContext.RestoreContext.Dispose</c> on certain GLX setups,
///     while passing every other log through.
/// </summary>
public sealed class SuppressGlxRenderExceptionLogSinkTests
{
    [Fact]
    public void Log_HarmlessGlxDisposeException_IsDropped()
    {
        var inner = new RecordingSink();
        var sink = new SuppressGlxRenderExceptionLogSink(inner);
        var ex = MakeSyncLockExceptionWithStackContaining(
            "   at Avalonia.X11.Glx.GlxContext.RestoreContext.Dispose()"
        );

        sink.Log(
            LogEventLevel.Error,
            "Visual",
            null,
            "Exception in render loop: '{0}' ({1})",
            ex,
            "DefaultRenderLoop #1"
        );

        Assert.Empty(inner.Messages);
    }

    [Fact]
    public void Log_SyncLockExceptionFromOtherStack_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressGlxRenderExceptionLogSink(inner);
        var ex = MakeSyncLockExceptionWithStackContaining("at MyApp.SomeOtherCode.Run()");

        sink.Log(LogEventLevel.Error, "Visual", null, "boom: {0}", ex);

        Assert.Equal(new[] { "boom: {0}" }, inner.Messages);
    }

    [Fact]
    public void Log_OtherRenderLoopException_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressGlxRenderExceptionLogSink(inner);
        var ex = new InvalidOperationException("something genuinely wrong");

        sink.Log(LogEventLevel.Error, "Visual", null, "Exception in render loop: {0}", ex);

        Assert.Equal(new[] { "Exception in render loop: {0}" }, inner.Messages);
    }

    [Fact]
    public void Log_NoPropertyValues_PassesThrough()
    {
        var inner = new RecordingSink();
        var sink = new SuppressGlxRenderExceptionLogSink(inner);

        sink.Log(LogEventLevel.Warning, "X11Platform", null, "Some plain message");

        Assert.Equal(new[] { "Some plain message" }, inner.Messages);
    }

    [Fact]
    public void Log_ExceptionWithNullStackTrace_PassesThrough()
    {
        // An exception that has never been thrown has StackTrace == null;
        // the predicate must not trip on those.
        var inner = new RecordingSink();
        var sink = new SuppressGlxRenderExceptionLogSink(inner);
        var ex = new SynchronizationLockException("not thrown");

        sink.Log(LogEventLevel.Error, "Visual", null, "boom: {0}", ex);

        Assert.Equal(new[] { "boom: {0}" }, inner.Messages);
    }

    [Fact]
    public void IsEnabled_DelegatesToInner()
    {
        var inner = new RecordingSink { IsEnabledResult = false };
        var sink = new SuppressGlxRenderExceptionLogSink(inner);

        Assert.False(sink.IsEnabled(LogEventLevel.Error, "Visual"));

        inner.IsEnabledResult = true;
        Assert.True(sink.IsEnabled(LogEventLevel.Error, "Visual"));
    }

    // Helper: produce a real, thrown SynchronizationLockException so it
    // has a non-null StackTrace, then wrap it in a new exception whose
    // stack we splice via Exception.SetRemoteStackTrace-equivalent
    // approach. Easiest path: throw and catch, then re-throw with the
    // required frame text injected into the message-based stack we read.
    //
    // SynchronizationLockException.StackTrace is a string; we cannot set
    // it directly, but if we throw it then catch it, the stack contains
    // *this* test method. To make it contain the GLX frame, we throw a
    // chained exception whose ToString() (which Avalonia formats into
    // the trace) includes the frame text. The production predicate
    // checks ex.StackTrace, so we substitute a subclass that returns a
    // canned stack string.
    private static SynchronizationLockException MakeSyncLockExceptionWithStackContaining(
        string frame
    )
    {
        return new StubSyncLockException(frame);
    }

    private sealed class StubSyncLockException : SynchronizationLockException
    {
        private readonly string _stackTrace;

        public StubSyncLockException(string stackTrace)
        {
            _stackTrace = stackTrace;
        }

        public override string? StackTrace => _stackTrace;
    }

    /// <summary>Inner sink that records the message templates it receives.</summary>
    private sealed class RecordingSink : ILogSink
    {
        public List<string> Messages { get; } = new();
        public bool IsEnabledResult { get; set; } = true;

        public bool IsEnabled(LogEventLevel level, string area)
        {
            return IsEnabledResult;
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Messages.Add(messageTemplate);
        }

        public void Log(
            LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues
        )
        {
            Messages.Add(messageTemplate);
        }
    }
}
