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
            var commands = new SystemCommandAvailabilityService(runner);
            commands.RaiseSnapshotChangedForTests(
                commands.GetSnapshot() with { HasFfmpeg = true }
            );
            runner.SupervisorInvocations.Clear();
            var service = new AudioFileService(commands, runner);

            var actual = await service.LoadAudioAsWavAsync(filePath);

            Assert.Equal(expected, actual);
            var invocation = Assert.Single(runner.SupervisorInvocations);
            Assert.Equal("ffmpeg", invocation.Command.FileName);
            Assert.Equal(
                [
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
            Assert.Null(invocation.Options.Timeout);
        }
        finally
        {
            TestPaths.DeleteDirectory(directory);
        }
    }
}
