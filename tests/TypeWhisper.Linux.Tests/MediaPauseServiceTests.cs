using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class MediaPauseServiceTests
{
    [Fact]
    public void ResumeMedia_retains_only_failed_player_for_dispose_retry()
    {
        const string players = """
            vlc Playing
            spotify Playing
            podcast Paused
            """;
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] firstPause = ["-p", "vlc", "pause"];
        string[] secondPause = ["-p", "spotify", "pause"];
        string[] nonPlayingPause = ["-p", "podcast", "pause"];
        string[] successfulPlay = ["-p", "vlc", "play"];
        string[] failedPlay = ["-p", "spotify", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        var failedPlayAttempts = 0;
        runner.FailWhen(
            (fileName, args) =>
                fileName == "playerctl"
                && args.SequenceEqual(failedPlay)
                && failedPlayAttempts++ == 0,
            "player unavailable"
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.Dispose();
        service.ResumeMedia();

        Assert.Equal(8, runner.Invocations.Count);
        Assert.Equal(status, runner.Invocations[0].Args);
        Assert.Equal(1, CountInvocations(runner, status));
        Assert.Equal(1, CountInvocations(runner, firstPause));
        Assert.Equal(1, CountInvocations(runner, secondPause));
        Assert.Equal(0, CountInvocations(runner, nonPlayingPause));
        Assert.Equal(1, CountInvocations(runner, successfulPlay));
        Assert.Equal(2, CountInvocations(runner, failedPlay));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal("playerctl", invocation.FileName)
        );
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromMilliseconds(1500), invocation.Timeout)
        );
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.Contains("spotify", StringComparison.Ordinal)
                        && message.Contains("ExitCode=1", StringComparison.Ordinal)
                        && message.Contains("player unavailable", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dispose_retries_persistently_failed_resume_without_follow_up_call()
    {
        const string players = "vlc Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] pause = ["-p", "vlc", "pause"];
        string[] play = ["-p", "vlc", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.FailWhen(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(play),
            "persistent resume failure"
        );
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        service.PauseMedia();
        service.ResumeMedia();
        service.Dispose();

        Assert.Equal(6, runner.Invocations.Count);
        Assert.Equal(1, CountInvocations(runner, status));
        Assert.Equal(1, CountInvocations(runner, pause));
        Assert.Equal(2, CountInvocations(runner, play));
    }

    [Fact]
    public void ResumeMedia_retains_timed_out_player_even_with_zero_exit_code()
    {
        const string players = "spotify Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] pause = ["-p", "spotify", "pause"];
        string[] play = ["-p", "spotify", "play"];
        var runner = new FakeProcessRunner
        {
            Default = new ProcessRunResult(
                true,
                true,
                0,
                string.Empty,
                "forced timeout"
            ),
        };
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(pause),
            string.Empty
        );
        var playAttempts = 0;
        runner.RespondWith(
            (fileName, args) =>
                fileName == "playerctl"
                && args.SequenceEqual(play)
                && playAttempts++ > 0,
            string.Empty
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.ResumeMedia();

        Assert.Equal(6, runner.Invocations.Count);
        Assert.Equal(1, CountInvocations(runner, status));
        Assert.Equal(1, CountInvocations(runner, pause));
        Assert.Equal(2, CountInvocations(runner, play));
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.Contains("spotify", StringComparison.Ordinal)
                        && message.Contains("TimedOut=true", StringComparison.Ordinal)
                        && message.Contains("forced timeout", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void PauseMedia_StillRunsWhileAnEarlierPlayerIsAwaitingResume()
    {
        const string players = "vlc Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] pause = ["-p", "vlc", "pause"];
        string[] play = ["-p", "vlc", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.FailWhen(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(play),
            "resume keeps failing"
        );
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        // vlc stays owed a resume after the first cycle; the next recording must still pause.
        service.PauseMedia();
        service.ResumeMedia();
        service.PauseMedia();

        Assert.Equal(2, CountInvocations(runner, status));
        Assert.Equal(2, CountInvocations(runner, pause));
        Assert.Equal(1, CountInvocations(runner, play));
    }

    [Fact]
    public void ResumeMedia_retires_player_after_three_failures()
    {
        const string players = "vlc Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] pause = ["-p", "vlc", "pause"];
        string[] play = ["-p", "vlc", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.FailWhen(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(play),
            "persistent resume failure"
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.ResumeMedia();
        service.ResumeMedia();
        service.ResumeMedia();

        Assert.Equal(1, CountInvocations(runner, status));
        Assert.Equal(1, CountInvocations(runner, pause));
        Assert.Equal(3, CountInvocations(runner, play));
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.EndsWith(
                            "Giving up after 3 attempts.",
                            StringComparison.Ordinal
                        )
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
        errorLog.Verify(
            log => log.AddEntry(It.IsAny<string>(), ErrorCategory.General),
            Times.Exactly(3)
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void ResumeMedia_missing_player_is_completed_without_retry_or_error()
    {
        const string players = "vlc Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] pause = ["-p", "vlc", "pause"];
        string[] play = ["-p", "vlc", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.FailWhen(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(play),
            "No players found\n"
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.ResumeMedia();
        service.PauseMedia();

        Assert.Equal(2, CountInvocations(runner, status));
        Assert.Equal(2, CountInvocations(runner, pause));
        Assert.Equal(1, CountInvocations(runner, play));
        errorLog.VerifyNoOtherCalls();
    }


    [Fact]
    public void ResumeMedia_other_failure_is_not_classified_as_missing()
    {
        const string players = "vlc Playing";
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] play = ["-p", "vlc", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.FailWhen(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(play),
            "No player could handle this command\n"
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.ResumeMedia();

        Assert.Equal(2, CountInvocations(runner, play));
        errorLog.Verify(
            log => log.AddEntry(It.IsAny<string>(), ErrorCategory.General),
            Times.Exactly(2)
        );
    }

    [Fact]
    public void Same_name_instances_pause_and_resume_only_the_playing_one()
    {
        const string players = """
            mpv Paused
            mpv.instance-zrsHlvMS Playing
            """;
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] list = ["-l"];
        string[] pause = ["-p", "mpv.instance-zrsHlvMS", "pause"];
        string[] play = ["-p", "mpv.instance-zrsHlvMS", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(list),
            "mpv\nmpv.instance-zrsHlvMS\n"
        );
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        service.PauseMedia();
        service.ResumeMedia();

        Assert.Equal(
            [status, pause, list, play],
            runner.Invocations.Select(invocation => invocation.Args.ToArray())
        );
    }

    [Fact]
    public void Base_instance_is_addressed_without_matching_its_siblings()
    {
        const string players = """
            mpv Playing
            mpv.instance-a Paused
            """;
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] list = ["-l"];
        string[] pause = ["-p", "mpv", "-i", "mpv.instance-a", "pause"];
        string[] play = ["-p", "mpv", "-i", "mpv.instance-a,mpv.instance-b", "play"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(list),
            "mpv\nmpv.instance-a\nmpv.instance-b\n"
        );
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        service.PauseMedia();
        service.ResumeMedia();

        Assert.Equal(
            [status, pause, list, play],
            runner.Invocations.Select(invocation => invocation.Args.ToArray())
        );
    }

    [Fact]
    public void ResumeMedia_vanished_base_instance_does_not_resume_a_sibling()
    {
        const string players = """
            mpv Playing
            mpv.instance-a Paused
            """;
        string[] status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
        string[] list = ["-l"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(status),
            players
        );
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(list),
            "mpv.instance-a\n"
        );
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        service.PauseMedia();
        service.ResumeMedia();
        service.ResumeMedia();

        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Args[^1] == "play");
        Assert.Equal(1, CountInvocations(runner, list));
        errorLog.VerifyNoOtherCalls();
    }

    private static int CountInvocations(FakeProcessRunner runner, IReadOnlyList<string> args)
    {
        return runner.Invocations.Count(invocation => invocation.Args.SequenceEqual(args));
    }
}
