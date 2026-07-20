using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SoundFeedbackServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Awaited_start_cue_uses_real_argv_and_finite_timeout_then_waits_for_runner()
    {
        using var sounds = new TemporarySoundsDirectory();
        var runner = new ControlledProcessRunner();
        var sut = new SoundFeedbackService(runner, "fake-player", sounds.Path);

        var playback = sut.PlayRecordingStartedAsync();
        await runner.Invoked.Task.WaitAsync(s_testGuard);

        Assert.False(playback.IsCompleted);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("fake-player", invocation.FileName);
        Assert.Equal([sounds.StartWavPath], invocation.Args);
        Assert.Equal(SoundFeedbackService.s_startCueTimeout, invocation.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), invocation.Timeout);
        Assert.Null(invocation.StandardInput);

        runner.Complete(new ProcessRunResult(true, false, 0, "", ""));
        await playback.WaitAsync(s_testGuard);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_cue_launch_failure_and_timeout_remain_best_effort(bool timedOut)
    {
        using var sounds = new TemporarySoundsDirectory();
        var result = timedOut
            ? new ProcessRunResult(true, true, -1, "", "")
            : new ProcessRunResult(false, false, -1, "", "launch failed");
        var runner = ControlledProcessRunner.WithImmediateResult(result);
        var sut = new SoundFeedbackService(runner, "fake-player", sounds.Path);

        await sut.PlayRecordingStartedAsync().WaitAsync(s_testGuard);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(SoundFeedbackService.s_startCueTimeout, invocation.Timeout);
    }

    [Fact]
    public async Task Start_cue_runner_exception_remains_best_effort()
    {
        using var sounds = new TemporarySoundsDirectory();
        var runner = ControlledProcessRunner.WithException(
            new InvalidOperationException("fake runner failure")
        );
        var sut = new SoundFeedbackService(runner, "fake-player", sounds.Path);

        await sut.PlayRecordingStartedAsync().WaitAsync(s_testGuard);

        Assert.Single(runner.Invocations);
    }

    [Theory]
    [InlineData("stop.wav")]
    [InlineData("success.wav")]
    [InlineData("error.wav")]
    public async Task Fire_and_forget_cues_use_real_argv_and_finite_timeout_without_waiting_for_runner(
        string cueFileName
    )
    {
        using var sounds = new TemporarySoundsDirectory();
        var runner = new ControlledProcessRunner();
        var sut = new SoundFeedbackService(runner, "fake-player", sounds.Path);

        var call = Task.Run(() => InvokeFireAndForgetCue(sut, cueFileName));
        await runner.Invoked.Task.WaitAsync(s_testGuard);
        await call.WaitAsync(s_testGuard);

        Assert.False(runner.Completion.IsCompleted);
        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("fake-player", invocation.FileName);
        Assert.Equal([sounds.PathFor(cueFileName)], invocation.Args);
        Assert.Equal(SoundFeedbackService.s_startCueTimeout, invocation.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), invocation.Timeout);
        Assert.Null(invocation.StandardInput);

        runner.Complete(new ProcessRunResult(true, false, 0, "", ""));
        await runner.Completion.WaitAsync(s_testGuard);
    }

    [Theory]
    [InlineData("stop.wav", RunnerOutcome.NotStarted)]
    [InlineData("stop.wav", RunnerOutcome.TimedOut)]
    [InlineData("stop.wav", RunnerOutcome.Exception)]
    [InlineData("success.wav", RunnerOutcome.NotStarted)]
    [InlineData("success.wav", RunnerOutcome.TimedOut)]
    [InlineData("success.wav", RunnerOutcome.Exception)]
    [InlineData("error.wav", RunnerOutcome.NotStarted)]
    [InlineData("error.wav", RunnerOutcome.TimedOut)]
    [InlineData("error.wav", RunnerOutcome.Exception)]
    public async Task Fire_and_forget_cue_failures_remain_best_effort(
        string cueFileName,
        RunnerOutcome outcome
    )
    {
        using var sounds = new TemporarySoundsDirectory();
        var runner = outcome switch
        {
            RunnerOutcome.NotStarted => ControlledProcessRunner.WithImmediateResult(
                new ProcessRunResult(false, false, -1, "", "launch failed")
            ),
            RunnerOutcome.TimedOut => ControlledProcessRunner.WithImmediateResult(
                new ProcessRunResult(true, true, -1, "", "")
            ),
            RunnerOutcome.Exception => ControlledProcessRunner.WithException(
                new InvalidOperationException("fake runner failure")
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };
        var sut = new SoundFeedbackService(runner, "fake-player", sounds.Path);

        var call = Task.Run(() => InvokeFireAndForgetCue(sut, cueFileName));
        await call.WaitAsync(s_testGuard);

        Assert.True(runner.Completion.IsCompleted);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public void Source_has_no_direct_process_path_and_observes_every_fire_and_forget_task()
    {
        var source = File.ReadAllText(SoundFeedbackServiceSourcePath());
        string[] directProcessPatterns =
        [
            @"\bProcess\s*\.\s*Start\b",
            @"\bProcessStartInfo\b",
            @"\bWaitForExit(?:Async)?\b",
            @"\bnew\s+Process\s*\("
        ];

        foreach (var pattern in directProcessPatterns)
        {
            Assert.False(
                Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant),
                $"SoundFeedbackService.cs contains a forbidden direct process path: {pattern}"
            );
        }

        AssertObservedCue(source, nameof(SoundFeedbackService.PlayRecordingStopped), "stop.wav");
        AssertObservedCue(source, nameof(SoundFeedbackService.PlaySuccess), "success.wav");
        AssertObservedCue(source, nameof(SoundFeedbackService.PlayError), "error.wav");
    }

    private static void InvokeFireAndForgetCue(SoundFeedbackService sut, string cueFileName)
    {
        switch (cueFileName)
        {
            case "stop.wav":
                sut.PlayRecordingStopped();
                break;
            case "success.wav":
                sut.PlaySuccess();
                break;
            case "error.wav":
                sut.PlayError();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cueFileName), cueFileName, null);
        }
    }

    private static void AssertObservedCue(string source, string methodName, string cueFileName)
    {
        var pattern =
            $@"public\s+void\s+{Regex.Escape(methodName)}\s*\(\s*\)\s*\{{\s*"
            // ReSharper disable once UseRawString -- interpolated regex with `{{` brace escapes; a raw string would need `$$"""` and re-escaping.
            + $@"Observe\s*\(\s*PlayAsync\s*\(\s*""{Regex.Escape(cueFileName)}""\s*,\s*"
            + @"s_startCueTimeout\s*\)\s*\)\s*;\s*\}";

        Assert.True(
            Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant),
            $"{methodName} must pass its {cueFileName} PlayAsync task to Observe."
        );
    }

    private static string SoundFeedbackServiceSourcePath([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(
                testDir,
                "..",
                "..",
                "src",
                "TypeWhisper.Linux",
                "Services",
                "SoundFeedbackService.cs"
            )
        );
    }

    public enum RunnerOutcome
    {
        NotStarted,
        TimedOut,
        Exception
    }

    private sealed class ControlledProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<ProcessRunResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource Invoked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public List<Invocation> Invocations { get; } = [];
        public Task<ProcessRunResult> Completion => _completion.Task;

        public static ControlledProcessRunner WithImmediateResult(ProcessRunResult result)
        {
            var runner = new ControlledProcessRunner();
            runner.Complete(result);
            return runner;
        }

        public static ControlledProcessRunner WithException(Exception exception)
        {
            var runner = new ControlledProcessRunner();
            runner.Fail(exception);
            return runner;
        }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default
        )
        {
            Invocations.Add(new Invocation(fileName, args.ToArray(), standardInput, timeout));
            Invoked.TrySetResult();
            return _completion.Task;
        }

        public void Complete(ProcessRunResult result)
        {
            _completion.TrySetResult(result);
        }

        private void Fail(Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private sealed record Invocation(
        string FileName,
        IReadOnlyList<string> Args,
        string? StandardInput,
        TimeSpan? Timeout
    );

    private sealed class TemporarySoundsDirectory : IDisposable
    {
        public TemporarySoundsDirectory()
        {
            Path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                $"typewhisper-sound-tests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(Path);
            StartWavPath = System.IO.Path.Join(Path, "start.wav");
            foreach (var fileName in new[] { "start.wav", "stop.wav", "success.wav", "error.wav" })
            {
                File.WriteAllBytes(PathFor(fileName), "RIFF"u8);
            }
        }

        public string Path { get; }
        public string StartWavPath { get; }

        public string PathFor(string fileName)
        {
            return System.IO.Path.Join(Path, fileName);
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
