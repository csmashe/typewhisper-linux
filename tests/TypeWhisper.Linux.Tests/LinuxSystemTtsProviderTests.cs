using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxSystemTtsProviderTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);
    private static readonly string[] s_audioPlaybackCommands = ["pw-play", "paplay", "aplay"];

    [Theory]
    [InlineData(null, "spoken text")]
    [InlineData("espeak", "   ")]
    public async Task SpeakAsync_returns_inactive_without_running_when_unavailable_or_text_is_whitespace(
        string? command,
        string text
    )
    {
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider(command, runner);

        var session = await provider.SpeakAsync(new TtsSpeakRequest(text), CancellationToken.None);

        Assert.False(session.IsActive);
        Assert.Empty(runner.Invocations);
        var completedCount = 0;
        session.Completed += (_, _) => completedCount++;
        Assert.Equal(1, completedCount);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("espeak-ng")]
    [InlineData("espeak")]
    public async Task SpeakAsync_routes_espeak_directly_with_text_as_one_argv_item(string command)
    {
        const string text = "one; \"two words\" $HOME";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider(command, runner);

        await provider.SpeakAsync(new TtsSpeakRequest(text), CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(command, invocation.FileName);
        Assert.Equal([text], invocation.Args);
        Assert.DoesNotContain("--stdout", invocation.Args);
        Assert.NotEqual("sh", invocation.FileName);
        Assert.DoesNotContain(invocation.FileName, s_audioPlaybackCommands);
        Assert.Equal(TimeSpan.FromSeconds(15), invocation.Timeout);
        Assert.Null(invocation.StandardInput);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("espeak-ng")]
    [InlineData("espeak")]
    public async Task SpeakAsync_routes_espeak_language_as_voice_selector(string command)
    {
        const string text = "Bonjour tout le monde";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider(command, runner);

        await provider.SpeakAsync(
            new TtsSpeakRequest(text, "fr"),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(command, invocation.FileName);
        Assert.Equal(["-v", "fr", text], invocation.Args);
        Assert.Null(invocation.StandardInput);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task SpeakAsync_routes_spd_say_with_wait_and_text_as_one_argv_item()
    {
        const string text = "keep this as one argument";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("spd-say", invocation.FileName);
        Assert.Equal(["--wait", text], invocation.Args);
        Assert.NotEqual("sh", invocation.FileName);
        Assert.DoesNotContain(invocation.FileName, s_audioPlaybackCommands);
        Assert.Equal(TimeSpan.FromSeconds(15), invocation.Timeout);
        Assert.Null(invocation.StandardInput);
        session.Stop();
        session.Stop();
        Assert.Single(runner.Invocations);
        Assert.Equal(0, runner.CancellationCount);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task SpeakAsync_trims_spd_say_language_and_keeps_text_as_one_argv_item()
    {
        const string text = "um; \"dois itens\" $HOME";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("spd-say", runner);

        await provider.SpeakAsync(
            new TtsSpeakRequest(text, " pt-BR "),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("spd-say", invocation.FileName);
        Assert.Equal(["--wait", "-l", "pt-BR", text], invocation.Args);
        Assert.NotEqual("sh", invocation.FileName);
        Assert.DoesNotContain(invocation.FileName, s_audioPlaybackCommands);
        Assert.Null(invocation.StandardInput);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public async Task SpeakAsync_uses_default_voice_when_language_has_no_usable_hint(
        string? language
    )
    {
        const string text = "default voice";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("espeak", runner);

        await provider.SpeakAsync(
            new TtsSpeakRequest(text, language),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal([text], invocation.Args);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task SpeakAsync_preserves_default_argv_for_unknown_command()
    {
        const string text = "unknown backend";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("custom-tts", runner);

        await provider.SpeakAsync(
            new TtsSpeakRequest(text, "de"),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal([text], invocation.Args);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Rejected_localized_invocation_retries_default_voice_once_with_remaining_timeout()
    {
        const string text = "fallback text";
        var runner = ControlledProcessRunner.WithImmediateResults(
            new ProcessRunResult(true, false, 23, "", "voice unavailable"),
            Success()
        );
        using var provider = CreateProvider("espeak-ng", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "nl-BE"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Equal(2, runner.Invocations.Length);
        var primary = runner.Invocations[0];
        var fallback = runner.Invocations[1];
        Assert.Equal("espeak-ng", primary.FileName);
        Assert.Equal(["-v", "nl-BE", text], primary.Args);
        Assert.Equal("espeak-ng", fallback.FileName);
        Assert.Equal([text], fallback.Args);
        Assert.NotNull(primary.Timeout);
        Assert.NotNull(fallback.Timeout);
        Assert.True(fallback.Timeout > TimeSpan.Zero);
        Assert.True(
            fallback.Timeout < primary.Timeout,
            $"Expected fallback timeout {fallback.Timeout} to be less than primary timeout {primary.Timeout}."
        );
        Assert.Null(primary.StandardInput);
        Assert.Null(fallback.StandardInput);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Rejected_localized_spd_say_invocation_retries_waiting_default_voice_once()
    {
        const string text = "fallback text";
        var runner = ControlledProcessRunner.WithImmediateResults(
            new ProcessRunResult(true, false, 23, "", "voice unavailable"),
            Success()
        );
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "nl-BE"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        session.Completed += (_, _) => completion.TrySetResult();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(2, runner.Invocations.Length);
        var primary = runner.Invocations[0];
        var fallback = runner.Invocations[1];
        Assert.Equal("spd-say", primary.FileName);
        Assert.Equal(["--wait", "-l", "nl-BE", text], primary.Args);
        Assert.Equal("spd-say", fallback.FileName);
        Assert.Equal(["--wait", text], fallback.Args);
        Assert.NotNull(primary.Timeout);
        Assert.NotNull(fallback.Timeout);
        Assert.True(fallback.Timeout > TimeSpan.Zero);
        Assert.True(
            fallback.Timeout < primary.Timeout,
            $"Expected fallback timeout {fallback.Timeout} to be less than primary timeout {primary.Timeout}."
        );
        Assert.Null(primary.StandardInput);
        Assert.Null(fallback.StandardInput);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("not-started")]
    public async Task Localized_invocation_does_not_retry_without_voice_rejection(string outcome)
    {
        var result = outcome switch
        {
            "success" => Success(),
            "not-started" => new ProcessRunResult(false, false, -1, "", "launch failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        var runner = ControlledProcessRunner.WithImmediateResult(result);
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("say once", "it"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        session.Completed += (_, _) => completion.TrySetResult();

        await completion.Task.WaitAsync(s_testGuard);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(["--wait", "-l", "it", "say once"], invocation.Args);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Localized_spd_say_timeout_runs_only_bounded_dispatcher_cancellation()
    {
        const string text = "say once";
        var runner = ControlledProcessRunner.WithImmediateResults(
            new ProcessRunResult(true, true, -1, "", ""),
            Success()
        );
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "it"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        session.Completed += (_, _) => completion.TrySetResult();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(2, runner.Invocations.Length);
        var primary = runner.Invocations[0];
        var cancellation = runner.Invocations[1];
        Assert.Equal("spd-say", primary.FileName);
        Assert.Equal(["--wait", "-l", "it", text], primary.Args);
        Assert.Equal(LinuxSystemTtsProvider.CalculatePlaybackTimeout(text.Length), primary.Timeout);
        Assert.Null(primary.StandardInput);
        AssertDispatcherCancellation(cancellation);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Timed_out_spd_say_fallback_runs_one_dispatcher_cancellation()
    {
        const string text = "fallback timeout";
        var runner = ControlledProcessRunner.WithImmediateResults(
            new ProcessRunResult(true, false, 23, "", "voice unavailable"),
            new ProcessRunResult(true, true, -1, "", ""),
            Success()
        );
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "sv"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        session.Completed += (_, _) => completion.TrySetResult();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(3, runner.Invocations.Length);
        Assert.Equal(["--wait", "-l", "sv", text], runner.Invocations[0].Args);
        Assert.Equal(["--wait", text], runner.Invocations[1].Args);
        AssertDispatcherCancellation(runner.Invocations[2]);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData(1, 15_000)]
    [InlineData(50, 15_000)]
    [InlineData(51, 15_200)]
    [InlineData(2_974, 599_800)]
    [InlineData(2_975, 600_000)]
    [InlineData(2_976, 600_000)]
    public async Task SpeakAsync_passes_clamped_finite_timeout_boundaries(
        int textLength,
        int expectedMilliseconds
    )
    {
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("espeak", runner);

        await provider.SpeakAsync(
            new TtsSpeakRequest(new string('x', textLength)),
            CancellationToken.None
        );

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), invocation.Timeout);
        Assert.True(invocation.Timeout > TimeSpan.Zero);
        Assert.True(invocation.Timeout <= TimeSpan.FromMinutes(10));
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public void CalculatePlaybackTimeout_is_overflow_safe_for_maximum_input_length()
    {
        var timeout = LinuxSystemTtsProvider.CalculatePlaybackTimeout(int.MaxValue);

        Assert.Equal(TimeSpan.FromMinutes(10), timeout);
    }

    [Fact]
    public async Task Pending_spd_say_waiter_is_active_until_utterance_invocation_completes()
    {
        var runner = new ControlledProcessRunner();
        using var provider = CreateProvider("spd-say", runner);
        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("pending"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        Assert.True(session.IsActive);
        Assert.Equal(["--wait", "pending"], Assert.Single(runner.Invocations).Args);
        Assert.False(completion.Task.IsCompleted);
        runner.CompleteInvocation(1, Success());

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        session.Stop();
        Assert.Equal(0, runner.CancellationCount);
        Assert.Single(runner.Invocations);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("espeak")]
    [InlineData("espeak-ng")]
    public async Task Stop_is_idempotent_for_espeak_without_dispatcher_control(string command)
    {
        var runner = new ControlledProcessRunner();
        using var provider = CreateProvider(command, runner);
        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("stop me"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        session.Stop();
        session.Stop();
        session.Stop();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, runner.CancellationCount);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(command, invocation.FileName);
        Assert.Equal(["stop me"], invocation.Args);
        Assert.DoesNotContain("-C", invocation.Args);
        Assert.DoesNotContain("-S", invocation.Args);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("caller-token")]
    public async Task Spd_say_cancellation_waits_for_one_bounded_dispatcher_control(
        string cancellationSource
    )
    {
        const string text = "cancel pending speech";
        var runner = ControlledProcessRunner.WithPendingResults(2);
        using var provider = CreateProvider("spd-say", runner);
        using var callerCts = new CancellationTokenSource();
        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "de"),
            callerCts.Token
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        if (cancellationSource == "stop")
        {
            session.Stop();
            session.Stop();
            session.Stop();
        }
        else
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel signals cancellation before the test proceeds; CancelAsync would defer callbacks.
            callerCts.Cancel();
        }

        // ReSharper disable once MethodSupportsCancellation -- the 2s guard must not be tied to callerCts; forwarding its token would abort the wait on the caller-cancel path.
        await runner.WaitForInvocationAsync(2).WaitAsync(s_testGuard);
        Assert.True(session.IsActive);
        Assert.False(completion.Task.IsCompleted);
        Assert.Equal(1, runner.CancellationCount);
        Assert.Equal(2, runner.Invocations.Length);
        Assert.Equal(
            ["--wait", "-l", "de", text],
            runner.Invocations[0].Args
        );
        AssertDispatcherCancellation(runner.Invocations[1]);

        runner.CompleteInvocation(2, Success());

        // ReSharper disable once MethodSupportsCancellation -- the 2s guard must not be tied to callerCts; forwarding its token would abort the wait on the caller-cancel path.
        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Equal(2, runner.Invocations.Length);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("timed-out")]
    [InlineData("throwing")]
    public async Task Dispatcher_control_failure_still_completes_once_without_recursion(
        string outcome
    )
    {
        // ReSharper disable once SuggestVarOrType_SimpleTypes -- the "throwing" arm is null, so the explicit nullable type carries nullability `var` would drop.
        ProcessRunResult? controlResult = outcome switch
        {
            "failed" => new ProcessRunResult(false, false, -1, "", "launch failed"),
            "timed-out" => new ProcessRunResult(true, true, -1, "", ""),
            "throwing" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        var runner = ControlledProcessRunner.WithPendingResults(2);
        using var provider = CreateProvider("spd-say", runner);
        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("best effort cleanup"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        session.Stop();
        await runner.WaitForInvocationAsync(2).WaitAsync(s_testGuard);
        if (controlResult is null)
        {
            runner.FailInvocation(2, new InvalidOperationException("control failed"));
        }
        else
        {
            runner.CompleteInvocation(2, controlResult);
        }

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Equal(2, runner.Invocations.Length);
        AssertDispatcherCancellation(runner.Invocations[1]);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Pending_default_voice_fallback_stays_active_and_stop_completes_once()
    {
        const string text = "stop fallback";
        var runner = ControlledProcessRunner.WithPendingResults(2);
        using var provider = CreateProvider("espeak", runner);
        var session = await provider.SpeakAsync(
            new TtsSpeakRequest(text, "pl"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        var completedCount = 0;
        session.Completed += (_, _) =>
        {
            // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
            Interlocked.Increment(ref completedCount);
            completion.TrySetResult();
        };

        Assert.True(session.IsActive);
        runner.CompleteInvocation(
            1,
            new ProcessRunResult(true, false, 17, "", "voice unavailable")
        );
        await runner.WaitForInvocationAsync(2).WaitAsync(s_testGuard);
        Assert.True(session.IsActive);
        Assert.Equal(2, runner.Invocations.Length);
        Assert.Equal(["-v", "pl", text], runner.Invocations[0].Args);
        Assert.Equal([text], runner.Invocations[1].Args);

        session.Stop();
        session.Stop();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, runner.CancellationCount);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Equal(2, runner.Invocations.Length);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Theory]
    [InlineData("not-started")]
    [InlineData("non-zero")]
    [InlineData("timed-out")]
    public async Task Failed_runner_results_end_session_and_complete_once(string failure)
    {
        var result = failure switch
        {
            "not-started" => new ProcessRunResult(false, false, -1, "", "launch failed"),
            "non-zero" => new ProcessRunResult(true, false, 23, "", "failed"),
            "timed-out" => new ProcessRunResult(true, true, -1, "", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var runner = ControlledProcessRunner.WithImmediateResult(result);
        using var provider = CreateProvider("espeak", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("result observation"),
            CancellationToken.None
        );
        var completedCount = 0;
        session.Completed += (_, _) => completedCount++;

        Assert.False(session.IsActive);
        Assert.Equal(1, completedCount);
        session.Stop();
        Assert.Equal(0, runner.CancellationCount);
        Assert.Single(runner.Invocations);
        Assert.False(runner.SurplusInvocationObserved);
    }

    [Fact]
    public async Task Stop_and_runner_completion_race_remains_idempotent()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var runner = new ControlledProcessRunner();
            using var provider = CreateProvider("espeak", runner);
            var session = await provider.SpeakAsync(
                new TtsSpeakRequest("race"),
                CancellationToken.None
            );
            var completion = NewCompletionSignal();
            var completedCount = 0;
            session.Completed += (_, _) =>
            {
                // ReSharper disable once AccessToModifiedClosure -- completedCount is deliberately shared between the completion handler and the test body (read via Volatile.Read); interlocked/volatile access is the intended synchronization.
                Interlocked.Increment(ref completedCount);
                completion.TrySetResult();
            };
            var start = NewCompletionSignal();

            var stopTask = Task.Run(async () =>
            {
                await start.Task;
                session.Stop();
                session.Stop();
            });
            var runnerTask = Task.Run(async () =>
            {
                await start.Task;
                runner.CompleteInvocation(1, Success());
            });
            start.TrySetResult();

            await Task.WhenAll(stopTask, runnerTask).WaitAsync(s_testGuard);
            await completion.Task.WaitAsync(s_testGuard);
            Assert.False(session.IsActive);
            Assert.Equal(1, Volatile.Read(ref completedCount));
            Assert.InRange(runner.CancellationCount, 0, 1);
            Assert.Single(runner.Invocations);
            Assert.False(runner.SurplusInvocationObserved);
        }
    }

    private static LinuxSystemTtsProvider CreateProvider(
        string? command,
        IProcessRunner processRunner
    )
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        return new LinuxSystemTtsProvider(settings.Object, processRunner, command);
    }

    private static void AssertDispatcherCancellation(Invocation invocation)
    {
        Assert.Equal("spd-say", invocation.FileName);
        Assert.Equal(["-C"], invocation.Args);
        Assert.Equal(TimeSpan.FromMilliseconds(500), invocation.Timeout);
        Assert.Null(invocation.StandardInput);
    }

    private static ProcessRunResult Success()
    {
        return new ProcessRunResult(true, false, 0, "", "");
    }

    private static TaskCompletionSource NewCompletionSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledProcessRunner : IProcessRunner
    {
        private readonly Lock _sync = new();
        private readonly ControlledResult[] _results;
        private readonly List<Invocation> _invocations = [];
        private int _cancellationCount;
        private int _invocationIndex;
        private int _surplusInvocationCount;

        public ControlledProcessRunner()
            : this(1) { }

        private ControlledProcessRunner(int resultCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resultCount);
            _results = Enumerable
                .Range(0, resultCount)
                .Select(_ => new ControlledResult())
                .ToArray();
        }

        public Invocation[] Invocations
        {
            get
            {
                lock (_sync)
                {
                    return _invocations.ToArray();
                }
            }
        }

        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        private int SurplusInvocationCount => Volatile.Read(ref _surplusInvocationCount);

        public bool SurplusInvocationObserved => SurplusInvocationCount != 0;

        public static ControlledProcessRunner WithImmediateResult(ProcessRunResult result)
        {
            return WithImmediateResults(result);
        }

        public static ControlledProcessRunner WithImmediateResults(
            params ProcessRunResult[] results
        )
        {
            var runner = new ControlledProcessRunner(results.Length);
            for (var index = 0; index < results.Length; index++)
            {
                runner.CompleteInvocation(index + 1, results[index]);
            }

            return runner;
        }

        public static ControlledProcessRunner WithPendingResults(int resultCount)
        {
            return new ControlledProcessRunner(resultCount);
        }

        public Task WaitForInvocationAsync(int invocationNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(invocationNumber, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(invocationNumber, _results.Length);
            return _results[invocationNumber - 1].Invoked.Task;
        }

        public void CompleteInvocation(int invocationNumber, ProcessRunResult result)
        {
            GetResult(invocationNumber).Completion.TrySetResult(result);
        }

        public void FailInvocation(int invocationNumber, Exception exception)
        {
            GetResult(invocationNumber).Completion.TrySetException(exception);
        }

        public async Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            bool detachAfterExit = false,
            CancellationToken ct = default
        )
        {
            var invocationIndex = Interlocked.Increment(ref _invocationIndex) - 1;
            lock (_sync)
            {
                _invocations.Add(new Invocation(fileName, args.ToArray(), standardInput, timeout));
            }

            if (invocationIndex >= _results.Length)
            {
                Interlocked.Increment(ref _surplusInvocationCount);
                throw new InvalidOperationException("No controlled process result remains.");
            }

            var result = _results[invocationIndex];

            result.Invoked.TrySetResult();
            if (result.Completion.Task.IsCompleted)
            {
                return await result.Completion.Task.ConfigureAwait(false);
            }

            try
            {
                return await result.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancellationCount);
                throw;
            }
        }

        private ControlledResult GetResult(int invocationNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(invocationNumber, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(invocationNumber, _results.Length);
            return _results[invocationNumber - 1];
        }

        private sealed class ControlledResult
        {
            public TaskCompletionSource<ProcessRunResult> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            public TaskCompletionSource Invoked { get; } = NewCompletionSignal();
        }
    }

    private sealed record Invocation(
        string FileName,
        IReadOnlyList<string> Args,
        string? StandardInput,
        TimeSpan? Timeout
    );
}
