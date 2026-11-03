using System.Reflection;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxStreamingTranscriptStateTests
{
    // The concurrency tests below hand off through TaskCompletionSources; bound every
    // coordination await so a regression fails the test instead of hanging the run.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(5);


    [Fact]
    public void StopSession_ReturnsConfirmedPollingText()
    {
        var sut = new StreamingTranscriptState();
        var session = sut.StartSession();

        var applied = sut.TryApplyPolling(session, "preview only", text => text, out var display);

        Assert.True(applied);
        Assert.Equal("preview only", display);
        Assert.Equal("preview only", sut.StopSession());
    }

    [Fact]
    public void StabilizeText_PreservesConfirmedPrefixWhenTranscriptGrows()
    {
        var result = StreamingTranscriptState.StabilizeText(
            "Hello world",
            "Hello world, how are you?"
        );

        Assert.Equal("Hello world, how are you?", result);
    }

    [Fact]
    public void StabilizeText_DoesNotAppendShortUnrelatedTextViaEmptyOverlap()
    {
        var result = StreamingTranscriptState.StabilizeText("abc", "xyz");

        Assert.Equal("xyz", result);
    }

    [Fact]
    public void TryApplyPolling_IgnoresStaleSessions()
    {
        var sut = new StreamingTranscriptState();
        var firstSession = sut.StartSession();
        var secondSession = sut.StartSession();

        var staleApplied = sut.TryApplyPolling(
            firstSession,
            "stale",
            text => text,
            out var staleDisplay
        );
        var currentApplied = sut.TryApplyPolling(
            secondSession,
            "fresh",
            text => text,
            out var currentDisplay
        );

        Assert.False(staleApplied);
        Assert.Equal("", staleDisplay);
        Assert.True(currentApplied);
        Assert.Equal("fresh", currentDisplay);
    }

    [Fact]
    public void TryApplyPolling_AppliesDictionaryCorrectionBeforePublishing()
    {
        var sut = new StreamingTranscriptState();
        var session = sut.StartSession();

        var applied = sut.TryApplyPolling(
            session,
            "teh world",
            text => text.Replace("teh", "the"),
            out var display
        );

        Assert.True(applied);
        Assert.Equal("the world", display);
    }

    [Fact]
    public async Task TryApplyPolling_ConcurrentOlderHypothesis_DoesNotClobberNewerCommittedText()
    {
        var sut = new StreamingTranscriptState();
        var session = sut.StartSession();
        sut.TryApplyPolling(session, "hello", text => text, out _);
        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callA = Task.Run(() =>
            sut.TryApplyPolling(
                session,
                "hello world",
                text =>
                {
                    aEntered.SetResult();
                    releaseA.Task.Wait();
                    return text;
                },
                out _
            )
        );

        await aEntered.Task.WaitAsync(s_testTimeout);
        var appliedB = sut.TryApplyPolling(
            session,
            "hello world how are you",
            text => text,
            out var displayB
        );
        releaseA.SetResult();
        var appliedA = await callA.WaitAsync(s_testTimeout);

        Assert.True(appliedB);
        Assert.Equal("hello world how are you", displayB);
        Assert.False(appliedA);
        Assert.Equal("hello world how are you", sut.StopSession());
    }

    [Fact]
    public async Task TryApplyPolling_StaleSessionCallback_NeverWritesAfterStopStart_EvenWhenNewSessionTextIsStillEmpty()
    {
        var sut = new StreamingTranscriptState();
        var firstSession = sut.StartSession();
        var staleEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseStale = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var staleDisplay = "";
        var staleCall = Task.Run(() =>
            sut.TryApplyPolling(
                firstSession,
                "stale text",
                text =>
                {
                    staleEntered.SetResult();
                    releaseStale.Task.Wait();
                    return text;
                },
                out staleDisplay
            )
        );

        // The stale corrector has snapshotted _confirmedText="" at the first session's version.
        await staleEntered.Task.WaitAsync(s_testTimeout);

        // After Stop/Start the new confirmed buffer is also "", so only the version check can
        // reject the stale write — release and await it before any session-2 commit.
        sut.StopSession();
        var secondSession = sut.StartSession();
        releaseStale.SetResult();
        var staleApplied = await staleCall.WaitAsync(s_testTimeout);

        Assert.False(staleApplied);
        Assert.Equal("", staleDisplay);

        // The new session still sees clean state and commits only its own text.
        var freshApplied = sut.TryApplyPolling(
            secondSession,
            "fresh text",
            text => text,
            out var freshDisplay
        );

        Assert.True(freshApplied);
        Assert.Equal("fresh text", freshDisplay);
        Assert.Equal("fresh text", sut.StopSession());
    }

    [Fact]
    public void StopSession_PrefersDisplayedPreviewWhenItDivergesFromConfirmed()
    {
        // Public TryApplyPolling sets _confirmedText and _lastDisplayedText
        // in lockstep, but the streaming pipeline can produce in-flight
        // states where the displayed overlay outruns the confirmed buffer
        // (the bug fix covers exactly this case). Reflect the divergent
        // state directly so the regression test pins the fixed StopSession
        // behavior — pre-fix this returned "" and silently dropped the
        // user's words on long dictations.
        var sut = new StreamingTranscriptState();
        sut.StartSession();

        SetPrivateField(sut, "_confirmedText", "hello");
        SetPrivateField(sut, "_lastDisplayedText", "hello world how are you");

        Assert.Equal("hello world how are you", sut.StopSession());
    }

    private static void SetPrivateField(StreamingTranscriptState target, string name, string value)
    {
        var field =
            typeof(StreamingTranscriptState).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new InvalidOperationException($"Field {name} not found.");
        field.SetValue(target, value);
    }
}
