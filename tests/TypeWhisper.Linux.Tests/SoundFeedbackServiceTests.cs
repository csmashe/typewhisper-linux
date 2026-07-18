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

    private sealed class ControlledProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<ProcessRunResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly Exception? _exception;
        private readonly ProcessRunResult? _immediateResult;

        public TaskCompletionSource Invoked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public List<Invocation> Invocations { get; } = [];

        public ControlledProcessRunner() { }

        private ControlledProcessRunner(ProcessRunResult immediateResult)
        {
            _immediateResult = immediateResult;
        }

        private ControlledProcessRunner(Exception exception)
        {
            _exception = exception;
        }

        public static ControlledProcessRunner WithImmediateResult(ProcessRunResult result)
        {
            return new ControlledProcessRunner(result);
        }

        public static ControlledProcessRunner WithException(Exception exception)
        {
            return new ControlledProcessRunner(exception);
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
            if (_exception is not null)
            {
                return Task.FromException<ProcessRunResult>(_exception);
            }

            return _immediateResult is not null
                ? Task.FromResult(_immediateResult)
                : _completion.Task;
        }

        public void Complete(ProcessRunResult result)
        {
            _completion.TrySetResult(result);
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
            File.WriteAllBytes(StartWavPath, "RIFF"u8);
        }

        public string Path { get; }
        public string StartWavPath { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
