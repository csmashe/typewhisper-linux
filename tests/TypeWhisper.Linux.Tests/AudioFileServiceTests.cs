using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioFileServiceTests
{
    [Fact]
    public async Task LoadAudioAsWavAsync_uses_discrete_argv_and_binary_capture()
    {
        var directory = TestPaths.CreateTempDirectory("audio-file-service");
        var filePath = Path.Join(directory, "voice \"$(touch nope)\".wav");
        var expected = new byte[] { 0, 1, 2, 255, 13, 10 };
        try
        {
            await File.WriteAllBytesAsync(filePath, [1]);
            var runner = new FakeProcessRunner
            {
                SupervisorDefault = new ProcessRunOutcome(
                    ProcessRunStatus.Exited,
                    0,
                    expected,
                    [],
                    ProcessOutputStatus.Complete,
                    null
                ),
            };
            var service = CreateService(runner);

            var actual = await service.LoadAudioAsWavAsync(filePath);

            Assert.Equal(expected, actual);
            var invocation = Assert.Single(runner.SupervisorInvocations);
            Assert.Equal("ffmpeg", invocation.Command.FileName);
            Assert.Equal(
                [
                    "-nostdin",
                    "-v",
                    "error",
                    "-i",
                    filePath,
                    "-vn",
                    "-ac",
                    "1",
                    "-ar",
                    "16000",
                    "-f",
                    "wav",
                    "pipe:1",
                ],
                invocation.Command.Arguments
            );
            Assert.Equal(
                ProcessCaptureMode.Binary,
                invocation.Options.StandardOutput
            );
            Assert.Equal(TimeSpan.FromMinutes(10), invocation.Options.Timeout);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task LoadAudioAsWavAsync_timed_out_conversion_throws_timeout()
    {
        var directory = TestPaths.CreateTempDirectory("audio-file-service");
        var filePath = Path.Join(directory, "clip.wav");
        try
        {
            await File.WriteAllBytesAsync(filePath, [1]);
            var runner = new FakeProcessRunner
            {
                SupervisorDefault = new ProcessRunOutcome(
                    ProcessRunStatus.TimedOut,
                    null,
                    [],
                    [],
                    ProcessOutputStatus.Complete,
                    null
                ),
            };
            var service = CreateService(runner);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                service.LoadAudioAsWavAsync(filePath)
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task IsSupportedAsync_known_extension_does_not_probe()
    {
        var runner = new FakeProcessRunner();
        var service = CreateService(runner);

        var supported = await service.IsSupportedAsync("recognized.wav");

        Assert.True(supported);
        Assert.Empty(runner.SupervisorInvocations);
    }

    [Fact]
    public async Task IsSupportedAsync_unrecognized_extension_is_unsupported_when_ffmpeg_is_missing()
    {
        var runner = new FakeProcessRunner();
        var commands = new SystemCommandAvailabilityService(runner);
        commands.RaiseSnapshotChangedForTests(
            commands.GetSnapshot() with { HasFfmpeg = false }
        );
        runner.SupervisorInvocations.Clear();
        var service = new AudioFileService(commands, runner);

        // Without ffmpeg the probe cannot run: the file is reported unsupported
        // (the HTTP handler's 400) rather than surfacing StartFailed as a 500.
        var supported = await service.IsSupportedAsync("mystery.bin");

        Assert.False(supported);
        Assert.Empty(runner.SupervisorInvocations);
    }

    [Fact]
    public async Task IsSupportedAsync_extensionless_audio_uses_bounded_discrete_probe_argv()
    {
        var directory = TestPaths.CreateTempDirectory("audio-file-service");
        var filePath = Path.Join(directory, "voice \"$(touch nope)\"");
        try
        {
            await File.WriteAllBytesAsync(filePath, [1]);
            var runner = new FakeProcessRunner
            {
                SupervisorDefault = new ProcessRunOutcome(
                    ProcessRunStatus.Exited,
                    0,
                    [],
                    [],
                    ProcessOutputStatus.Complete,
                    null
                ),
            };
            var service = CreateService(runner);

            var supported = await service.IsSupportedAsync(filePath);

            Assert.True(supported);
            var invocation = Assert.Single(runner.SupervisorInvocations);
            Assert.Equal("ffmpeg", invocation.Command.FileName);
            Assert.Equal(
                [
                    "-nostdin",
                    "-v",
                    "error",
                    "-i",
                    filePath,
                    "-map",
                    "0:a:0",
                    "-frames:a",
                    "1",
                    "-f",
                    "null",
                    "-",
                ],
                invocation.Command.Arguments
            );
            Assert.Equal(
                ProcessCaptureMode.Discard,
                invocation.Options.StandardOutput
            );
            Assert.Equal(
                ProcessCaptureMode.Discard,
                invocation.Options.StandardError
            );
            Assert.Equal(TimeSpan.FromSeconds(10), invocation.Options.Timeout);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task IsSupportedAsync_completed_nonzero_probe_returns_false()
    {
        var runner = new FakeProcessRunner
        {
            SupervisorDefault = new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                1,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            ),
        };
        var service = CreateService(runner);

        var supported = await service.IsSupportedAsync("not-audio");

        Assert.False(supported);
        Assert.Single(runner.SupervisorInvocations);
    }

    [Fact]
    public async Task IsSupportedAsync_start_failure_propagates()
    {
        var runner = new FakeProcessRunner
        {
            SupervisorDefault = new ProcessRunOutcome(
                ProcessRunStatus.StartFailed,
                null,
                [],
                [],
                ProcessOutputStatus.Complete,
                "fake: ffmpeg could not start"
            ),
        };
        var service = CreateService(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IsSupportedAsync("unrecognized")
        );

        Assert.Equal("fake: ffmpeg could not start", error.Message);
    }

    [Fact]
    public async Task IsSupportedAsync_timed_out_probe_throws_timeout()
    {
        var runner = new FakeProcessRunner
        {
            SupervisorDefault = new ProcessRunOutcome(
                ProcessRunStatus.TimedOut,
                null,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            ),
        };
        var service = CreateService(runner);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.IsSupportedAsync("unrecognized")
        );
    }

    [Fact]
    public async Task IsSupportedAsync_cancellation_propagates()
    {
        var runner = new FakeProcessRunner();
        var service = CreateService(runner);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.IsSupportedAsync("unrecognized", cancellation.Token)
        );

        Assert.Empty(runner.SupervisorInvocations);
    }

    [Fact]
    public async Task LoadAudioAsWavAsync_extensionless_file_proceeds_to_ffmpeg_decode()
    {
        var directory = TestPaths.CreateTempDirectory("audio-file-service");
        var filePath = Path.Join(directory, "extensionless-audio");
        try
        {
            await File.WriteAllBytesAsync(filePath, [1]);
            var runner = new FakeProcessRunner
            {
                SupervisorDefault = new ProcessRunOutcome(
                    ProcessRunStatus.Exited,
                    0,
                    [1, 2, 3],
                    [],
                    ProcessOutputStatus.Complete,
                    null
                ),
            };
            var service = CreateService(runner);

            var audio = await service.LoadAudioAsWavAsync(filePath);

            Assert.Equal([1, 2, 3], audio);
            var invocation = Assert.Single(runner.SupervisorInvocations);
            Assert.Equal("ffmpeg", invocation.Command.FileName);
            Assert.Equal(filePath, invocation.Command.Arguments[4]);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }

    private static AudioFileService CreateService(FakeProcessRunner runner)
    {
        var commands = new SystemCommandAvailabilityService(runner);
        commands.RaiseSnapshotChangedForTests(
            commands.GetSnapshot() with { HasFfmpeg = true }
        );
        runner.Invocations.Clear();
        runner.SupervisorInvocations.Clear();
        return new AudioFileService(commands, runner);
    }
}
