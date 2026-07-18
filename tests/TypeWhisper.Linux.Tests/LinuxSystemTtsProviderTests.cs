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
    }

    [Fact]
    public async Task SpeakAsync_routes_spd_say_through_runner_with_existing_argument_contract()
    {
        const string text = "keep this as one argument";
        var runner = ControlledProcessRunner.WithImmediateResult(Success());
        using var provider = CreateProvider("spd-say", runner);

        await provider.SpeakAsync(new TtsSpeakRequest(text), CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("spd-say", invocation.FileName);
        Assert.Equal([text], invocation.Args);
        Assert.Equal(TimeSpan.FromSeconds(15), invocation.Timeout);
        Assert.Null(invocation.StandardInput);
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
        Assert.Equal(["-l", "pt-BR", text], invocation.Args);
        Assert.NotEqual("sh", invocation.FileName);
        Assert.DoesNotContain(invocation.FileName, s_audioPlaybackCommands);
        Assert.Null(invocation.StandardInput);
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
        Assert.Equal(2, runner.Invocations.Count);
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
    }

    [Theory]
    [InlineData("success")]
    [InlineData("not-started")]
    [InlineData("timed-out")]
    public async Task Localized_invocation_does_not_retry_without_voice_rejection(string outcome)
    {
        var result = outcome switch
        {
            "success" => Success(),
            "not-started" => new ProcessRunResult(false, false, -1, "", "launch failed"),
            "timed-out" => new ProcessRunResult(true, true, -1, "", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        var runner = ControlledProcessRunner.WithImmediateResults(result, Success());
        using var provider = CreateProvider("spd-say", runner);

        var session = await provider.SpeakAsync(
            new TtsSpeakRequest("say once", "it"),
            CancellationToken.None
        );
        var completion = NewCompletionSignal();
        session.Completed += (_, _) => completion.TrySetResult();

        await completion.Task.WaitAsync(s_testGuard);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(["-l", "it", "say once"], invocation.Args);
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
    }

    [Fact]
    public void CalculatePlaybackTimeout_is_overflow_safe_for_maximum_input_length()
    {
        var timeout = LinuxSystemTtsProvider.CalculatePlaybackTimeout(int.MaxValue);

        Assert.Equal(TimeSpan.FromMinutes(10), timeout);
    }

    [Fact]
    public async Task Pending_runner_is_active_and_success_completes_once()
    {
        var runner = new ControlledProcessRunner();
        using var provider = CreateProvider("espeak-ng", runner);
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
        runner.Complete(Success());

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        session.Stop();
        Assert.Equal(0, runner.CancellationCount);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_cancels_pending_runner_once()
    {
        var runner = new ControlledProcessRunner();
        using var provider = CreateProvider("espeak", runner);
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
        runner.Complete(new ProcessRunResult(true, false, 17, "", "voice unavailable"));
        await runner.WaitForInvocationAsync(2).WaitAsync(s_testGuard);
        Assert.True(session.IsActive);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(["-v", "pl", text], runner.Invocations[0].Args);
        Assert.Equal([text], runner.Invocations[1].Args);

        session.Stop();
        session.Stop();

        await completion.Task.WaitAsync(s_testGuard);
        Assert.False(session.IsActive);
        Assert.Equal(1, runner.CancellationCount);
        Assert.Equal(1, Volatile.Read(ref completedCount));
        Assert.Equal(2, runner.Invocations.Count);
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
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
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
                runner.Complete(Success());
            });
            start.TrySetResult();

            await Task.WhenAll(stopTask, runnerTask).WaitAsync(s_testGuard);
            await completion.Task.WaitAsync(s_testGuard);
            Assert.False(session.IsActive);
            Assert.Equal(1, Volatile.Read(ref completedCount));
            Assert.InRange(runner.CancellationCount, 0, 1);
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
        private readonly ControlledResult[] _results;
        private int _cancellationCount;
        private int _completionIndex;
        private int _invocationIndex;

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

        public List<Invocation> Invocations { get; } = [];
        public int CancellationCount => Volatile.Read(ref _cancellationCount);

        public static ControlledProcessRunner WithImmediateResult(ProcessRunResult result)
        {
            return WithImmediateResults(result);
        }

        public static ControlledProcessRunner WithImmediateResults(
            params ProcessRunResult[] results
        )
        {
            var runner = new ControlledProcessRunner(results.Length);
            foreach (var result in results)
            {
                runner.Complete(result);
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
            if (invocationNumber > _results.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(invocationNumber));
            }

            return _results[invocationNumber - 1].Invoked.Task;
        }

        public async Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default
        )
        {
            var invocationIndex = Interlocked.Increment(ref _invocationIndex) - 1;
            if (invocationIndex >= _results.Length)
            {
                throw new InvalidOperationException("No controlled process result remains.");
            }

            var result = _results[invocationIndex];
            Invocations.Add(new Invocation(fileName, args.ToArray(), standardInput, timeout));
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

        public void Complete(ProcessRunResult result)
        {
            var completionIndex = Interlocked.Increment(ref _completionIndex) - 1;
            if (completionIndex >= _results.Length)
            {
                throw new InvalidOperationException("No controlled process completion remains.");
            }

            _results[completionIndex].Completion.TrySetResult(result);
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
