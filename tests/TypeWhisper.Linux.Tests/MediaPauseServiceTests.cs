using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class MediaPauseServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(10);
    private static readonly string[] s_status = ["-a", "--format", "{{playerInstance}} {{status}}", "status"];
    private static readonly string[] s_spotifyPause = ["-p", "spotify", "pause"];
    private static readonly string[] s_spotifyPlay = ["-p", "spotify", "play"];
    private static readonly string[] s_vlcPause = ["-p", "vlc", "pause"];
    private static readonly string[] s_vlcPlay = ["-p", "vlc", "play"];
    private static readonly string[] s_mpvPause = ["-p", "mpv", "pause"];

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

    [Fact]
    public void ResumeMedia_arriving_mid_pause_resumes_the_player_once_the_pause_finishes()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(s_status),
            "spotify Playing\n"
        );
        MediaPauseService? service = null;
        var raced = false;
        // Side-effecting matcher, never matching: lands a ResumeMedia after the pause command
        // was sent but before PauseMedia records the player, which is what shutdown can do.
        runner.RespondWith(
            (_, args) =>
            {
                // ReSharper disable once InvertIf -- inverting would duplicate the trailing return.
                if (!raced && args.SequenceEqual(s_spotifyPause))
                {
                    raced = true;
                    // ReSharper disable once AccessToModifiedClosure -- the matcher must call back
                    // into the service constructed below; that back-reference is the seam under test.
                    service!.ResumeMedia();
                }

                return false;
            },
            string.Empty
        );
        service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        service.PauseMedia();

        Assert.Equal(1, CountInvocations(runner, s_spotifyPlay));
        // The deferred resume completed, so a later resume has nothing left to undo.
        service.ResumeMedia();
        Assert.Equal(1, CountInvocations(runner, s_spotifyPlay));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Players_paused_before_a_pause_command_throws_are_still_resumed(bool resumeArrivesMidPause)
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(s_status),
            "spotify Playing\nvlc Playing\n"
        );
        MediaPauseService? service = null;
        // Side-effecting matcher, never matching: optionally lands a ResumeMedia mid-pause,
        // then makes vlc's pause throw after spotify was already paused.
        runner.RespondWith(
            (_, args) =>
            {
                // ReSharper disable once InvertIf -- inverting would duplicate the trailing return.
                if (args.SequenceEqual(s_vlcPause))
                {
                    if (resumeArrivesMidPause)
                    {
                        // ReSharper disable once AccessToModifiedClosure -- the matcher must call back
                        // into the service constructed below; that back-reference is the seam under test.
                        service!.ResumeMedia();
                    }

                    throw new InvalidOperationException("playerctl crashed");
                }

                return false;
            },
            string.Empty
        );
        service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        service.PauseMedia();
        if (!resumeArrivesMidPause)
        {
            service.ResumeMedia();
        }

        Assert.Equal(1, CountInvocations(runner, s_spotifyPlay));
    }

    [Fact]
    public async Task ResumeMediaForShutdown_waits_for_the_running_pause_to_exit_before_resuming()
    {
        using var shutdownWaiting = new ManualResetEventSlim();
        var runner = CreateThreePlayerRunner();
        var spotifyPause = runner.Hold(s_spotifyPause);
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>())
        {
            ShutdownWaitStarting = shutdownWaiting.Set,
        };

        var pause = Task.Run(service.PauseMedia);
        Assert.True(spotifyPause.Launched.Wait(s_testGuard));
        var shutdown = Task.Run(() =>
        {
            service.ResumeMediaForShutdown(s_testGuard);
            return runner.InvocationsSnapshot();
        });
        Assert.True(shutdownWaiting.Wait(s_testGuard));
        Assert.False(shutdown.IsCompleted);

        var exitedAt = runner.InvocationsSnapshot().Length;
        spotifyPause.ExitWith();
        var invocationsAtReturn = await shutdown.WaitAsync(s_testGuard);
        await pause.WaitAsync(s_testGuard);

        // Shutdown exits as soon as this returns, so the resume follows the pause's exit.
        Assert.True(IndexOf(invocationsAtReturn, s_spotifyPlay) >= exitedAt);
        // The pause stopped after its running command instead of pausing the next player.
        Assert.Equal(0, CountInvocations(runner, s_vlcPause));
        service.PauseMedia();
        Assert.Equal(1, CountInvocations(runner, s_status));
    }

    [Fact]
    public async Task ResumeMediaForShutdown_does_not_resume_a_player_whose_pause_failed()
    {
        using var shutdownWaiting = new ManualResetEventSlim();
        var runner = CreateThreePlayerRunner();
        var spotifyPause = runner.Hold(s_spotifyPause);
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>())
        {
            ShutdownWaitStarting = shutdownWaiting.Set,
        };

        var pause = Task.Run(service.PauseMedia);
        Assert.True(spotifyPause.Launched.Wait(s_testGuard));
        var shutdown = Task.Run(() => service.ResumeMediaForShutdown(s_testGuard));
        Assert.True(shutdownWaiting.Wait(s_testGuard));
        spotifyPause.ExitWith(exitCode: 1);
        await shutdown.WaitAsync(s_testGuard);
        await pause.WaitAsync(s_testGuard);

        Assert.Equal(0, CountInvocations(runner, s_spotifyPlay));
    }

    [Fact]
    public async Task A_pause_held_between_its_shutdown_check_and_its_launch_is_ordered_before_the_final_resume()
    {
        using var pauseAtLaunch = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        using var shutdownWaiting = new ManualResetEventSlim();
        var runner = CreateThreePlayerRunner();
        var spotifyPause = runner.Hold(s_spotifyPause);
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>())
        {
            ShutdownWaitStarting = shutdownWaiting.Set,
            PauseCommandLaunching = () =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the hook runs on the pause task, which is awaited before the events are disposed.
                pauseAtLaunch.Set();
                // ReSharper disable once AccessToDisposedClosure -- the hook runs on the pause task, which is awaited before the events are disposed.
                releaseLaunch.Wait(s_testGuard);
            },
        };

        var pause = Task.Run(service.PauseMedia);
        Assert.True(pauseAtLaunch.Wait(s_testGuard));
        // The pause is released only once shutdown is blocked on the gate it holds for the launch.
        var shutdown = await BlockedThread.StartAndWaitUntilBlocked(
            () =>
            {
                service.ResumeMediaForShutdown(s_testGuard);
                return runner.InvocationsSnapshot();
            },
            s_testGuard
        );
        Assert.False(shutdownWaiting.IsSet);
        releaseLaunch.Set();

        // Shutdown cannot slip in between the check and the launch, so it always finds the pause.
        Assert.True(shutdownWaiting.Wait(s_testGuard));
        Assert.True(spotifyPause.Launched.IsSet);
        Assert.False(shutdown.IsCompleted);
        var exitedAt = runner.InvocationsSnapshot().Length;
        spotifyPause.ExitWith();
        var invocationsAtReturn = await shutdown.WaitAsync(s_testGuard);
        await pause.WaitAsync(s_testGuard);

        Assert.True(IndexOf(invocationsAtReturn, s_spotifyPlay) >= exitedAt);
        Assert.Equal(0, CountInvocations(runner, s_vlcPause));
    }

    [Fact]
    public async Task A_pause_that_outlasts_shutdowns_wait_is_resumed_once_it_exits()
    {
        var runner = CreateThreePlayerRunner();
        var vlcPause = runner.Hold(s_vlcPause);
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>());

        var pause = Task.Run(service.PauseMedia);
        Assert.True(vlcPause.Launched.Wait(s_testGuard));
        var invocationsAtReturn = await Task.Run(() =>
            {
                service.ResumeMediaForShutdown(TimeSpan.Zero);
                return runner.InvocationsSnapshot();
            })
            .WaitAsync(s_testGuard);

        // Spotify was paused before the running command, so it plays again before shutdown returns.
        Assert.Contains(invocationsAtReturn, invocation => invocation.Args.SequenceEqual(s_spotifyPlay));
        Assert.False(pause.IsCompleted);

        vlcPause.ExitWith();
        await pause.WaitAsync(s_testGuard);

        // The pause landed after shutdown's resume, so the pause resumed vlc itself.
        Assert.True(IndexOf(runner.InvocationsSnapshot(), s_vlcPlay) >= invocationsAtReturn.Length);
        Assert.Equal(1, CountInvocations(runner, s_vlcPlay));
        Assert.Equal(0, CountInvocations(runner, s_mpvPause));
        service.PauseMedia();
        Assert.Equal(1, CountInvocations(runner, s_status));
    }

    [Fact]
    public async Task A_second_shutdown_that_settles_a_late_pause_still_resumes_the_player()
    {
        using var pauseAtSettle = new ManualResetEventSlim();
        using var releaseSettle = new ManualResetEventSlim();
        var runner = CreateThreePlayerRunner();
        var vlcPause = runner.Hold(s_vlcPause);
        var service = new MediaPauseService(runner, Mock.Of<IErrorLogService>())
        {
            PauseCommandExited = () =>
            {
                if (!vlcPause.Launched.IsSet)
                {
                    return;
                }

                // ReSharper disable once AccessToDisposedClosure -- the hook runs on the pause task, which is awaited before the events are disposed.
                pauseAtSettle.Set();
                // ReSharper disable once AccessToDisposedClosure -- the hook runs on the pause task, which is awaited before the events are disposed.
                releaseSettle.Wait(s_testGuard);
            },
        };

        var pause = Task.Run(service.PauseMedia);
        Assert.True(vlcPause.Launched.Wait(s_testGuard));
        await Task.Run(() => service.ResumeMediaForShutdown(TimeSpan.Zero)).WaitAsync(s_testGuard);
        var exitedAt = runner.InvocationsSnapshot().Length;
        vlcPause.ExitWith();
        // The pause has exited but the worker has not settled it when the second shutdown runs.
        Assert.True(pauseAtSettle.Wait(s_testGuard));
        await Task.Run(() => service.ResumeMediaForShutdown(TimeSpan.Zero)).WaitAsync(s_testGuard);

        // The second shutdown resumed vlc itself, while the pause worker was still held.
        Assert.True(IndexOf(runner.InvocationsSnapshot(), s_vlcPlay) >= exitedAt);
        Assert.Equal(1, CountInvocations(runner, s_vlcPlay));

        releaseSettle.Set();
        await pause.WaitAsync(s_testGuard);

        Assert.Equal(1, CountInvocations(runner, s_vlcPlay));
    }

    [Theory]
    // The base instance vanished and a sibling appeared: nothing is resumed.
    [InlineData("mpv.instance-b\n", null)]
    // The base instance lives on: the new sibling is excluded.
    [InlineData("mpv\nmpv.instance-b\n", "-p mpv -i mpv.instance-b play")]
    public async Task A_late_pause_resume_targets_the_instances_live_when_it_runs(
        string liveInstances,
        string? expectedPlay
    )
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(s_status),
            "mpv Playing\n"
        );
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(["-l"]),
            liveInstances
        );
        var mpvPause = runner.Hold(s_mpvPause);
        var errorLog = new Mock<IErrorLogService>();
        var service = new MediaPauseService(runner, errorLog.Object);

        var pause = Task.Run(service.PauseMedia);
        Assert.True(mpvPause.Launched.Wait(s_testGuard));
        await Task.Run(() => service.ResumeMediaForShutdown(TimeSpan.Zero)).WaitAsync(s_testGuard);
        mpvPause.ExitWith();
        await pause.WaitAsync(s_testGuard);

        var plays = runner
            .InvocationsSnapshot()
            .Where(invocation => invocation.Args[^1] == "play")
            .Select(invocation => string.Join(' ', invocation.Args))
            .ToArray();
        Assert.Equal(expectedPlay is null ? [] : [expectedPlay], plays);
        errorLog.VerifyNoOtherCalls();
    }

    // Spotify, vlc and mpv are playing.
    private static FakeProcessRunner CreateThreePlayerRunner()
    {
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "playerctl" && args.SequenceEqual(s_status),
            "spotify Playing\nvlc Playing\nmpv Playing\n"
        );
        return runner;
    }

    private static int IndexOf(FakeProcessRunner.Invocation[] invocations, string[] args)
    {
        return Array.FindIndex(invocations, invocation => invocation.Args.SequenceEqual(args));
    }

    private static int CountInvocations(FakeProcessRunner runner, IReadOnlyList<string> args)
    {
        return runner.InvocationsSnapshot().Count(invocation => invocation.Args.SequenceEqual(args));
    }
}
