using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed partial class PcmPlaybackServiceTests
{
    private static readonly TimeSpan s_guard = TimeSpan.FromSeconds(3);

    [Fact]
    public void Resolver_requires_executable_candidates_and_preserves_canonical_player_order()
    {
        var root = TestPaths.CreateTempDirectory("pcm-player-resolution");
        var first = Path.Join(root, "first");
        var second = Path.Join(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        try
        {
            CreateCandidate(first, "pw-play", executable: false);
            var paplay = CreateCandidate(first, "paplay", executable: true);
            var pwPlay = CreateCandidate(second, "pw-play", executable: true);
            var path = string.Join(Path.PathSeparator, first, second);

            var resolved = PcmPlayerResolver.Resolve(path);

            Assert.NotNull(resolved);
            Assert.Equal(PcmPlayerKind.PwPlay, resolved.Kind);
            Assert.Equal(Path.GetFullPath(pwPlay), resolved.AbsolutePath);

            SetExecutable(pwPlay, executable: false);
            resolved = PcmPlayerResolver.Resolve(path);

            Assert.NotNull(resolved);
            Assert.Equal(PcmPlayerKind.Paplay, resolved.Kind);
            Assert.Equal(Path.GetFullPath(paplay), resolved.AbsolutePath);
        }
        finally
        {
            TestPaths.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Pw_play_only_path_keeps_snapshot_cue_and_pcm_discovery_in_agreement()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var root = TestPaths.CreateTempDirectory("pcm-player-agreement");
        try
        {
            var pwPlay = CreateCandidate(root, "pw-play", executable: true);
            Environment.SetEnvironmentVariable("PATH", root);

            var commands = new SystemCommandAvailabilityService(new FakeProcessRunner());
            var sound = new SoundFeedbackService(new FakeProcessRunner());
            var supervisor = new RecordingPcmSupervisor();
            var pcm = new PcmPlaybackService(supervisor, PcmPlayerResolver.Resolve);

            Assert.True(commands.GetSnapshot().HasAudioPlayer);
            Assert.Equal(Path.GetFullPath(pwPlay), sound.PlayerPath);
            Assert.True(pcm.IsAvailable);
            Assert.Equal(PcmPlayerKind.PwPlay, PcmPlayerResolver.Resolve()?.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TestPaths.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Streams_exact_argv_owned_bytes_and_eof_without_waiting_for_backpressure(
        int kindValue
    )
    {
        var kind = (PcmPlayerKind)kindValue;
        var process = new ControlledPcmProcessSession { BlockWrites = true };
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var path = $"/validated/{PlayerName(kind)}";
        var sut = CreateService(supervisor, kind, path);
        var source = new byte[] { 1, 2, 3, 4 };

        var playTask = sut.PlayAsync(
            new PcmPlaybackRequest(
                source,
                24_000,
                2,
                PcmSampleFormat.Signed16LittleEndian
            ),
            CancellationToken.None
        );
        var playback = await playTask.WaitAsync(s_guard);
        await process.WriteStarted.Task.WaitAsync(s_guard);

        // The --raw probe runs off-thread, so the task may yield. What must still hold is that the
        // session copied the payload before returning — overwriting the caller's buffer proves it.
        source.AsSpan().Fill(99);
        var invocation = Assert.Single(supervisor.Starts);
        Assert.Equal(path, invocation.Command.FileName);
        Assert.Equal(ExpectedArguments(kind), invocation.Command.Arguments);
        Assert.True(invocation.Options.RedirectStandardInput);
        Assert.Equal(ProcessSessionOutputMode.Lines, invocation.Options.StandardError);

        process.ReleaseWrite();
        await process.InputCompleted.Task.WaitAsync(s_guard);
        Assert.Equal([1, 2, 3, 4], process.StandardInput.ToArray());
        Assert.Equal(1, process.CompleteInputCount);

        var completed = 0;
        playback.Completed += (_, _) => completed++;
        process.Complete(new ProcessExitOutcome(ProcessExitReason.Exited, 0));
        await WaitUntilAsync(() => completed == 1);
        Assert.False(playback.IsActive);
    }

    [Fact]
    public async Task PwPlay_without_raw_flag_support_omits_it_and_still_streams_from_stdin()
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var sut = CreateService(supervisor, supportsRawFlag: false);

        await sut.PlayAsync(ValidRequest(), CancellationToken.None);

        var invocation = Assert.Single(supervisor.Starts);
        Assert.Equal(
            ["--rate=24000", "--channels=1", "--format=s16", "-"],
            invocation.Command.Arguments
        );
        Assert.DoesNotContain("--raw", invocation.Command.Arguments);
    }

    [Theory]
    [InlineData("  -a, --raw   Raw samples will be read", true)]
    [InlineData("  -q, --quality  Resampler quality", false)]
    public async Task PwPlay_raw_flag_is_probed_from_the_usage_screen(string usage, bool expected)
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor
        {
            NextSession = process,
            ProbeStandardOutput = usage,
        };
        // The default probe path is exercised here, so no override is supplied.
        var sut = new PcmPlaybackService(
            supervisor,
            () => new ResolvedPcmPlayer(PcmPlayerKind.PwPlay, $"/validated/pw-play-{expected}")
        );

        await sut.PlayAsync(ValidRequest(), CancellationToken.None);

        var probe = Assert.Single(supervisor.Probes);
        Assert.Equal(["--help"], probe.Arguments);
        Assert.Equal(
            expected,
            supervisor.Starts.Single().Command.Arguments.Contains("--raw")
        );
    }

    [Fact]
    public void Playback_source_has_no_temp_file_or_file_write_path()
    {
        var source = File.ReadAllText(PcmPlaybackServiceSourcePath());

        Assert.DoesNotContain("GetTempPath", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(FileWriteApiPattern(), source);
    }

    [Fact]
    public async Task Validation_rejects_invalid_rate_channels_alignment_format_and_empty_payload()
    {
        var supervisor = new RecordingPcmSupervisor();
        var sut = CreateService(supervisor);
        PcmPlaybackRequest[] invalid =
        [
            new(new byte[2], 0, 1, PcmSampleFormat.Signed16LittleEndian),
            new(new byte[2], 24_000, 0, PcmSampleFormat.Signed16LittleEndian),
            new(new byte[2], 24_000, 2, PcmSampleFormat.Signed16LittleEndian),
            new(new byte[2], 24_000, 1, (PcmSampleFormat)999),
            new(ReadOnlyMemory<byte>.Empty, 24_000, 1, PcmSampleFormat.Signed16LittleEndian),
        ];

        foreach (var request in invalid)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                sut.PlayAsync(request, CancellationToken.None)
            );
        }

        Assert.Empty(supervisor.Starts);
    }

    [Fact]
    public void Float_conversion_is_bit_exact_with_original_supertonic_algorithm()
    {
        float[] values =
        [
            -2.0f,
            -1.0f,
            -0.5f,
            -1.0f / short.MaxValue,
            -0.0f,
            0.0f,
            1.0f / short.MaxValue,
            0.5f,
            1.0f,
            2.0f,
        ];
        var floatBytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                floatBytes.AsSpan(i * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[i])
            );
        }

        var expected = OldSupertonicConvert(values);
        var actual = PcmPlaybackService.ConvertFloat32ToPcm16LittleEndian(floatBytes);

        Assert.Equal(expected, actual);
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(actual));
        Assert.Equal(
            short.MaxValue,
            BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(actual.Length - 2))
        );
    }

    [Fact]
    public async Task Precancel_does_not_start_a_player()
    {
        var supervisor = new RecordingPcmSupervisor();
        var sut = CreateService(supervisor);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.PlayAsync(ValidRequest(), cancellation.Token)
        );
        Assert.Empty(supervisor.Starts);
    }

    [Fact]
    public async Task Cancelling_during_the_raw_flag_probe_starts_no_player()
    {
        var supervisor = new RecordingPcmSupervisor();
        using var cancellation = new CancellationTokenSource();
        var probeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseProbe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // The probe deliberately ignores its token, standing in for one already blocked inside
        // RunProbe: cancellation has to be caught after it returns, not only before it starts.
        var sut = new PcmPlaybackService(
            supervisor,
            () => new ResolvedPcmPlayer(PcmPlayerKind.PwPlay, "/validated/pw-play"),
            (_, _) =>
            {
                probeEntered.TrySetResult();
                releaseProbe.Task.GetAwaiter().GetResult();
                return true;
            }
        );

        var playTask = sut.PlayAsync(ValidRequest(), cancellation.Token);
        await probeEntered.Task.WaitAsync(s_guard);
        await cancellation.CancelAsync();
        releaseProbe.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playTask);
        Assert.Empty(supervisor.Starts);
    }

    [Fact]
    public async Task Cancellation_during_feed_cancels_feeder_and_terminates_once()
    {
        var process = new ControlledPcmProcessSession { BlockWrites = true };
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var sut = CreateService(supervisor);
        using var cancellation = new CancellationTokenSource();
        var playback = await sut.PlayAsync(ValidRequest(), cancellation.Token);
        // ReSharper disable once MethodSupportsCancellation -- s_guard is the test's own deadline;
        // the only token in scope is the one under test, and tying the wait to it would abort the
        // very handshake this test cancels afterwards.
        await process.WriteStarted.Task.WaitAsync(s_guard);

        await cancellation.CancelAsync();
        await WaitUntilAsync(() => process.TerminateCount == 1);
        process.Complete(new ProcessExitOutcome(ProcessExitReason.Terminated, null));
        await WaitForCompletionAsync(playback);

        Assert.Equal(1, process.TerminateCount);
        Assert.Equal(0, process.CompleteInputCount);
    }

    [Fact]
    public async Task Double_stop_terminates_once_and_completed_is_raised_once()
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );
        var completed = 0;
        playback.Completed += (_, _) => completed++;

        playback.Stop();
        playback.Stop();
        process.Complete(new ProcessExitOutcome(ProcessExitReason.Terminated, null));
        await WaitUntilAsync(() => completed == 1);

        Assert.Equal(1, process.TerminateCount);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Stop_completion_race_raises_completed_once()
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );
        var completed = 0;
        playback.Completed += (_, _) => Interlocked.Increment(ref completed);

        await Task.WhenAll(
            Task.Run(playback.Stop),
            Task.Run(() => process.Complete(
                new ProcessExitOutcome(ProcessExitReason.Exited, 0)
            ))
        );
        await WaitUntilAsync(() => Volatile.Read(ref completed) == 1);

        playback.Stop();
        Assert.Equal(1, completed);
        Assert.InRange(process.TerminateCount, 0, 1);
    }

    [Fact]
    public async Task Start_failure_returns_an_inactive_completed_session()
    {
        var supervisor = new RecordingPcmSupervisor { StartError = "fake start failure" };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );

        Assert.False(playback.IsActive);
        var completed = 0;
        playback.Completed += (_, _) => completed++;
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Broken_pipe_is_observed_and_terminates_the_session()
    {
        var process = new ControlledPcmProcessSession
        {
            WriteException = new IOException("fake broken pipe"),
        };
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );

        await WaitUntilAsync(() => process.TerminateCount == 1);
        process.Complete(new ProcessExitOutcome(ProcessExitReason.Terminated, null));
        await WaitForCompletionAsync(playback);

        Assert.False(playback.IsActive);
    }

    [Fact]
    public async Task Nonzero_exit_completes_the_playback_session_once()
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );
        var completed = 0;
        playback.Completed += (_, _) => completed++;

        process.Complete(
            new ProcessExitOutcome(ProcessExitReason.Exited, 23),
            new ProcessOutputLine(ProcessStream.StandardError, new string('x', 4_096))
        );
        await WaitUntilAsync(() => completed == 1);

        Assert.False(playback.IsActive);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task Subscribing_after_completion_replays_it_to_the_late_subscriber()
    {
        var process = new ControlledPcmProcessSession();
        var supervisor = new RecordingPcmSupervisor { NextSession = process };
        var playback = await CreateService(supervisor).PlayAsync(
            ValidRequest(),
            CancellationToken.None
        );
        var early = 0;
        playback.Completed += (_, _) => Interlocked.Increment(ref early);

        process.Complete(new ProcessExitOutcome(ProcessExitReason.Exited, 0));
        // Waiting on the early subscriber proves completion has already been dispatched.
        await WaitUntilAsync(() => Volatile.Read(ref early) == 1);

        var late = 0;
        playback.Completed += (_, _) => late++;

        Assert.Equal(1, late);
        Assert.Equal(1, Volatile.Read(ref early));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plugin_scope_stop_or_retirement_terminates_playback(bool retire)
    {
        var process = new ControlledPcmProcessSession();
        var runner = new FakeProcessRunner
        {
            SessionFactory = (_, _) => new ProcessSessionStartOutcome(process, null),
        };
        var scope = new PluginProcessSupervisorScope("pcm-test-plugin", runner);
        var sut = new PcmPlaybackService(
            scope,
            () => new ResolvedPcmPlayer(PcmPlayerKind.PwPlay, "/validated/pw-play"),
            (_, _) => true
        );
        var playback = await sut.PlayAsync(ValidRequest(), CancellationToken.None);

        if (retire)
        {
            scope.Retire();
        }
        else
        {
            scope.TerminateAll();
        }

        Assert.Equal(1, process.TerminateCount);
        process.Complete(new ProcessExitOutcome(ProcessExitReason.Terminated, null));
        await WaitForCompletionAsync(playback);
    }

    private static PcmPlaybackService CreateService(
        RecordingPcmSupervisor supervisor,
        PcmPlayerKind kind = PcmPlayerKind.PwPlay,
        string path = "/validated/pw-play",
        bool supportsRawFlag = true
    )
    {
        return new PcmPlaybackService(
            supervisor,
            () => new ResolvedPcmPlayer(kind, path),
            (_, _) => supportsRawFlag
        );
    }

    private static PcmPlaybackRequest ValidRequest()
    {
        return new PcmPlaybackRequest(
            new byte[] { 1, 2 },
            24_000,
            1,
            PcmSampleFormat.Signed16LittleEndian
        );
    }

    private static IReadOnlyList<string> ExpectedArguments(PcmPlayerKind kind)
    {
        return kind switch
        {
            PcmPlayerKind.PwPlay =>
                ["--raw", "--rate=24000", "--channels=2", "--format=s16", "-"],
            PcmPlayerKind.Paplay =>
                ["--raw", "--rate=24000", "--channels=2", "--format=s16le"],
            PcmPlayerKind.Aplay =>
                ["--file-type=raw", "--format=S16_LE", "--rate=24000", "--channels=2"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static string PlayerName(PcmPlayerKind kind)
    {
        return kind switch
        {
            PcmPlayerKind.PwPlay => "pw-play",
            PcmPlayerKind.Paplay => "paplay",
            PcmPlayerKind.Aplay => "aplay",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static byte[] OldSupertonicConvert(float[] values)
    {
        var result = new byte[values.Length * sizeof(short)];
        for (var i = 0; i < values.Length; i++)
        {
            var clamped = Math.Max(-1.0f, Math.Min(1.0f, values[i]));
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(
                result.AsSpan(i * sizeof(short), sizeof(short)),
                sample
            );
        }

        return result;
    }

    private static string CreateCandidate(string directory, string name, bool executable)
    {
        var path = Path.Join(directory, name);
        File.WriteAllText(path, "fake player");
        SetExecutable(path, executable);
        return path;
    }

    private static void SetExecutable(string path, bool executable)
    {
#pragma warning disable CA1416 // TypeWhisper.Linux.Tests targets the Linux host.
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | (executable ? UnixFileMode.UserExecute : 0)
        );
#pragma warning restore CA1416
    }

    private static async Task WaitForCompletionAsync(ITtsPlaybackSession playback)
    {
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        playback.Completed += (_, _) => completed.TrySetResult();
        if (!playback.IsActive)
        {
            completed.TrySetResult();
        }

        await completed.Task.WaitAsync(s_guard);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_guard;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static string PcmPlaybackServiceSourcePath(
        [CallerFilePath] string thisFile = ""
    )
    {
        return Path.GetFullPath(
            Path.Join(
                Path.GetDirectoryName(thisFile)!,
                "..",
                "..",
                "src",
                "TypeWhisper.Linux",
                "Services",
                "PcmPlaybackService.cs"
            )
        );
    }

    [GeneratedRegex(@"\bFile\s*\.\s*(?:Write|Create|Open)", RegexOptions.CultureInvariant)]
    private static partial Regex FileWriteApiPattern();

    private sealed class RecordingPcmSupervisor : IPluginProcessSupervisor
    {
        public ControlledPcmProcessSession? NextSession { get; init; }
        public string? StartError { get; init; }
        public string ProbeStandardOutput { get; init; } = string.Empty;
        public List<(ProcessCommand Command, ProcessSessionOptions Options)> Starts { get; } = [];
        public List<ProcessCommand> Probes { get; } = [];

        public ProcessSessionStartOutcome StartSession(
            ProcessCommand command,
            ProcessSessionOptions options
        )
        {
            Starts.Add((command, options));
            return new ProcessSessionStartOutcome(NextSession, StartError);
        }

        public ProcessRunOutcome RunProbe(
            ProcessCommand command,
            ProcessOneShotOptions options,
            CancellationToken cancellationToken = default
        )
        {
            Probes.Add(command);
            return new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                0,
                Encoding.UTF8.GetBytes(ProbeStandardOutput),
                [],
                ProcessOutputStatus.Complete,
                null
            );
        }

        public Task<ProcessRunOutcome> RunOneShotAsync(
            ProcessCommand command,
            ProcessOneShotOptions options,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public DetachedLaunchOutcome LaunchDetached(ProcessCommand command) =>
            throw new NotSupportedException();

        public DetachedLaunchOutcome LaunchUri(Uri uri) =>
            throw new NotSupportedException();
    }

    private sealed class ControlledPcmProcessSession : IPluginProcessSession
    {
        private readonly TaskCompletionSource<ProcessExitOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<ProcessOutputLine> _output =
            Channel.CreateUnbounded<ProcessOutputLine>();
        private readonly TaskCompletionSource _writeGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BlockWrites { get; init; }
        public Exception? WriteException { get; init; }
        public TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource InputCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public List<byte> StandardInput { get; } = [];

        // Written from the feeder and the cancellation callback, read from the test thread.
        // Atomic writes with acquire reads so a WaitUntilAsync poll cannot cache a stale value.
        private int _completeInputCount;
        private int _terminateCount;
        public int CompleteInputCount => Volatile.Read(ref _completeInputCount);
        public int TerminateCount => Volatile.Read(ref _terminateCount);
        public int ProcessId => 1234;
        public bool IsRunning => !_completion.Task.IsCompleted;
        public Task<ProcessExitOutcome> Completion => _completion.Task;

        public async IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await foreach (
                var line in _output.Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return line;
            }
        }

        public async ValueTask WriteStandardInputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        )
        {
            WriteStarted.TrySetResult();
            if (BlockWrites)
            {
                await _writeGate.Task.WaitAsync(cancellationToken);
            }

            if (WriteException is not null)
            {
                throw WriteException;
            }

            StandardInput.AddRange(data.ToArray());
        }

        public ValueTask CompleteStandardInputAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _completeInputCount);
            InputCompleted.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void Terminate()
        {
            Interlocked.Increment(ref _terminateCount);
        }

        public void Dispose()
        {
            Terminate();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ReleaseWrite()
        {
            _writeGate.TrySetResult();
        }

        public void Complete(
            ProcessExitOutcome outcome,
            params ProcessOutputLine[] output
        )
        {
            foreach (var line in output)
            {
                _output.Writer.TryWrite(line);
            }

            _output.Writer.TryComplete();
            _completion.TrySetResult(outcome);
        }
    }
}
