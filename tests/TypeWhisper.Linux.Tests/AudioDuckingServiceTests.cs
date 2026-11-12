using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioDuckingServiceTests
{
    private const string ServerCookie = "3922:700c";

    [Fact]
    public void DuckAndRestore_preserve_channel_vectors_for_each_sink_input()
    {
        const string listing = """
            Sink Input #593
                Volume: front-left: 45875 / 70% / -9.30 dB,   front-right: 26214 / 40% / -23.88 dB
                Base Volume: 65536 / 100% / 0.00 dB
            Sink Input #42
                Volume: mono: 32769 / 50% / -18.06 dB
            """;
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) =>
                fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        ScriptServerCookies(runner);
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>());

        service.DuckAudio(0.5f);
        service.RestoreAudio();

        Assert.Equal(8, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Equal("pactl", invocation.FileName));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromMilliseconds(1500), invocation.Timeout)
        );
        Assert.Equal(["info"], runner.Invocations[0].Args);
        Assert.Equal(["list", "sink-inputs"], runner.Invocations[1].Args);
        Assert.Equal(["info"], runner.Invocations[2].Args);
        Assert.Equal(
            ["set-sink-input-volume", "593", "22938", "13107"],
            runner.Invocations[3].Args
        );
        Assert.Equal(
            ["set-sink-input-volume", "42", "16385"],
            runner.Invocations[4].Args
        );
        Assert.Equal(["info"], runner.Invocations[5].Args);
        Assert.Equal(
            ["set-sink-input-volume", "593", "45875", "26214"],
            runner.Invocations[6].Args
        );
        Assert.Equal(
            ["set-sink-input-volume", "42", "32769"],
            runner.Invocations[7].Args
        );

        var stereoInvocations = runner.Invocations
            .Where(invocation => invocation.Args.Count > 1 && invocation.Args[1] == "593")
            .ToArray();
        Assert.Equal(2, stereoInvocations.Length);
        Assert.DoesNotContain(
            stereoInvocations,
            invocation =>
                invocation.Args.SequenceEqual(["set-sink-input-volume", "593", "70%"]) ||
                invocation.Args.SequenceEqual(["set-sink-input-volume", "593", "45875"])
        );
    }

    [Fact]
    public void RestoreAudio_retains_only_failed_vectors_for_dispose_retry()
    {
        const string listing = """
            Sink Input #10
                Volume: front-left: 40000 / 61% / -12.00 dB,   front-right: 20000 / 31% / -25.00 dB
            Sink Input #20
                Volume: front-left: 30001 / 46% / -17.00 dB,   front-right: 10003 / 15% / -32.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] firstDuck = ["set-sink-input-volume", "10", "20000", "10000"];
        string[] secondDuck = ["set-sink-input-volume", "20", "15001", "5002"];
        string[] successfulRestore = ["set-sink-input-volume", "10", "40000", "20000"];
        string[] failedRestore = ["set-sink-input-volume", "20", "30001", "10003"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        var failedRestoreAttempts = 0;
        runner.FailWhen(
            (fileName, args) =>
                fileName == "pactl"
                && args.SequenceEqual(failedRestore)
                && failedRestoreAttempts++ == 0,
            "restore denied"
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.Dispose();
        service.RestoreAudio();

        Assert.Equal(10, runner.Invocations.Count);
        Assert.Equal(1, CountInvocations(runner, list));
        Assert.Equal(1, CountInvocations(runner, firstDuck));
        Assert.Equal(1, CountInvocations(runner, secondDuck));
        Assert.Equal(1, CountInvocations(runner, successfulRestore));
        Assert.Equal(2, CountInvocations(runner, failedRestore));
        Assert.All(runner.Invocations, invocation => Assert.Equal("pactl", invocation.FileName));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromMilliseconds(1500), invocation.Timeout)
        );
        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Args.Any(argument => argument.EndsWith('%'))
        );
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.Contains("sink input 20", StringComparison.Ordinal)
                        && message.Contains("ExitCode=1", StringComparison.Ordinal)
                        && message.Contains("restore denied", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dispose_retries_persistently_failed_restore_without_follow_up_call()
    {
        const string listing = """
            Sink Input #30
                Volume: mono: 48000 / 73% / -8.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] duck = ["set-sink-input-volume", "30", "24000"];
        string[] restore = ["set-sink-input-volume", "30", "48000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        runner.FailWhen(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(restore),
            "persistent restore failure"
        );
        ScriptServerCookies(runner);
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>());

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.Dispose();

        Assert.Equal(8, runner.Invocations.Count);
        Assert.Equal(1, CountInvocations(runner, list));
        Assert.Equal(1, CountInvocations(runner, duck));
        Assert.Equal(2, CountInvocations(runner, restore));
    }

    [Fact]
    public void RestoreAudio_retains_timed_out_vector_even_with_zero_exit_code()
    {
        const string listing = """
            Sink Input #7
                Volume: mono: 55555 / 85% / -4.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] restore = ["set-sink-input-volume", "7", "55555"];
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
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(2, CountInvocations(runner, restore));
        Assert.All(runner.Invocations, invocation => Assert.Equal("pactl", invocation.FileName));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromMilliseconds(1500), invocation.Timeout)
        );
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.Contains("sink input 7", StringComparison.Ordinal)
                        && message.Contains("TimedOut=true", StringComparison.Ordinal)
                        && message.Contains("forced timeout", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Exactly(2)
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void RestoreAudio_retires_persistently_failed_input_after_three_attempts_and_releases_guard()
    {
        const string listing = """
            Sink Input #55
                Volume: mono: 48000 / 73% / -8.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] duck = ["set-sink-input-volume", "55", "24000"];
        string[] restore = ["set-sink-input-volume", "55", "48000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        runner.FailWhen(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(restore),
            "persistent restore failure"
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();
        service.RestoreAudio();
        service.RestoreAudio();
        service.DuckAudio(0.5f);

        Assert.Equal(2, CountInvocations(runner, list));
        Assert.Equal(2, CountInvocations(runner, duck));
        Assert.Equal(3, CountInvocations(runner, restore));
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

    [Theory]
    [InlineData("Failure: No such entity\n")]
    [InlineData("Failed to get sink input information: No such entity\n")]
    public void RestoreAudio_missing_input_is_completed_without_retry_or_error(string missingError)
    {
        const string listing = """
            Sink Input #56
                Volume: mono: 40000 / 61% / -12.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] duck = ["set-sink-input-volume", "56", "20000"];
        string[] restore = ["set-sink-input-volume", "56", "40000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        runner.FailWhen(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(restore),
            missingError
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();
        service.DuckAudio(0.5f);

        Assert.Equal(2, CountInvocations(runner, list));
        Assert.Equal(2, CountInvocations(runner, duck));
        Assert.Equal(1, CountInvocations(runner, restore));
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void RestoreAudio_success_clears_state_and_fresh_cycle_starts_at_zero()
    {
        const string listing = """
            Sink Input #57
                Volume: mono: 36000 / 55% / -14.00 dB
            """;
        string[] list = ["list", "sink-inputs"];
        string[] duck = ["set-sink-input-volume", "57", "18000"];
        string[] restore = ["set-sink-input-volume", "57", "36000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(list),
            listing
        );
        ScriptServerCookies(runner);
        var service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>());

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();
        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(2, CountInvocations(runner, list));
        Assert.Equal(2, CountInvocations(runner, duck));
        Assert.Equal(2, CountInvocations(runner, restore));
        Assert.Equal(12, runner.Invocations.Count);
    }


    [Theory]
    [InlineData("Failure: Access denied\n")]
    [InlineData("Failed to get sink input information: Access denied\n")]
    public void RestoreAudio_other_failure_is_not_classified_as_missing(string otherError)
    {
        const string listing = """
            Sink Input #56
                Volume: mono: 40000 / 61% / -12.00 dB
            """;
        string[] restore = ["set-sink-input-volume", "56", "40000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        runner.FailWhen(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(restore),
            otherError
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(2, CountInvocations(runner, restore));
        errorLog.Verify(
            log => log.AddEntry(It.IsAny<string>(), ErrorCategory.General),
            Times.Exactly(2)
        );
    }

    [Fact]
    public void RestoreAudio_counters_are_per_entry()
    {
        const string listing = """
            Sink Input #56
                Volume: mono: 40000 / 61% / -12.00 dB
            Sink Input #57
                Volume: mono: 30000 / 46% / -18.00 dB
            """;
        string[] restoreHealthy = ["set-sink-input-volume", "56", "40000"];
        string[] restoreStuck = ["set-sink-input-volume", "57", "30000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        runner.FailWhen(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(restoreStuck),
            "persistent restore failure"
        );
        ScriptServerCookies(runner);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        service.RestoreAudio();
        service.RestoreAudio();
        service.RestoreAudio();

        // The healthy entry restores once; the stuck one is retried to eviction and a
        // global counter would have retired it early via the healthy entry's failure-free
        // passes.
        Assert.Equal(1, CountInvocations(runner, restoreHealthy));
        Assert.Equal(3, CountInvocations(runner, restoreStuck));
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.EndsWith("Giving up after 3 attempts.", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
    }

    [Fact]
    public void RestoreAudio_arriving_mid_duck_runs_once_the_duck_finishes()
    {
        const string listing = """
            Sink Input #593
                Volume: mono: 45875 / 70% / -9.30 dB
            """;
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) =>
                fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        AudioDuckingService? service = null;
        var raced = false;
        // Side-effecting matcher, never matching: the only seam that lands a RestoreAudio
        // between the duck's listing and its set-volume, which is what shutdown does.
        runner.RespondWith(
            (_, args) =>
            {
                // ReSharper disable once InvertIf -- inverting would duplicate the trailing return.
                if (!raced && args.Count > 0 && args[0] == "set-sink-input-volume")
                {
                    raced = true;
                    // ReSharper disable once AccessToModifiedClosure -- the matcher must call back
                    // into the service constructed below; that back-reference is the seam under test.
                    service!.RestoreAudio();
                }

                return false;
            },
            string.Empty
        );
        ScriptServerCookies(runner);
        service = new AudioDuckingService(runner, Mock.Of<IErrorLogService>());

        service.DuckAudio(0.5f);

        // The deferred restore must run inside DuckAudio, after the ducking set-volume.
        Assert.Equal(["info"], runner.Invocations[0].Args);
        Assert.Equal(
            ["list", "sink-inputs"],
            runner.Invocations[1].Args
        );
        Assert.Equal(["info"], runner.Invocations[2].Args);
        Assert.Equal(["set-sink-input-volume", "593", "22938"], runner.Invocations[3].Args);
        Assert.Equal(["info"], runner.Invocations[4].Args);
        Assert.Equal(["set-sink-input-volume", "593", "45875"], runner.Invocations[5].Args);
        Assert.Equal(6, runner.Invocations.Count);

        // The deferred restore completed, so a later restore has nothing left to undo.
        service.RestoreAudio();
        Assert.Equal(6, runner.Invocations.Count);
    }

    [Fact]
    public void RestoreAudio_after_server_restart_retires_entries_instead_of_restoring_reused_ids()
    {
        const string listing = """
            Sink Input #0
                Volume: front-left: 40001 / 61% / -12.86 dB,   front-right: 59999 / 92% / -2.30 dB
            """;
        string[] info = ["info"];
        string[] restore = ["set-sink-input-volume", "0", "40001", "59999"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        ScriptServerCookies(runner, ServerCookie, ServerCookie, "296a:0996");
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.4f);
        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(0, CountInvocations(runner, restore));
        Assert.Equal(3, CountInvocations(runner, info));
        errorLog.VerifyNoOtherCalls();
    }

    [Fact]
    public void RestoreAudio_with_unchanged_cookie_restores_and_unreadable_cookie_is_retried()
    {
        const string listing = """
            Sink Input #12
                Volume: mono: 40000 / 61% / -12.00 dB
            """;
        string[] info = ["info"];
        string[] restore = ["set-sink-input-volume", "12", "40000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        ScriptServerCookies(runner, ServerCookie, ServerCookie, null);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();
        Assert.Equal(0, CountInvocations(runner, restore));

        service.RestoreAudio();
        service.RestoreAudio();

        Assert.Equal(1, CountInvocations(runner, restore));
        Assert.Equal(4, CountInvocations(runner, info));
        errorLog.Verify(
            log =>
                log.AddEntry(
                    It.Is<string>(message =>
                        message.Contains("sink input 12", StringComparison.Ordinal)
                        && message.Contains("could not read the audio server cookie", StringComparison.Ordinal)
                    ),
                    ErrorCategory.General
                ),
            Times.Once
        );
        errorLog.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, ServerCookie)]
    [InlineData(ServerCookie, null)]
    [InlineData(ServerCookie, "296a:0996")]
    public void DuckAudio_touches_no_stream_unless_the_same_cookie_is_read_before_and_after_the_listing(
        string? cookieBeforeListing,
        string? cookieAfterListing
    )
    {
        const string listing = """
            Sink Input #0
                Volume: mono: 40000 / 61% / -12.00 dB
            """;
        string[] duck = ["set-sink-input-volume", "0", "20000"];
        var runner = new FakeProcessRunner();
        runner.RespondWith(
            (fileName, args) => fileName == "pactl" && args.SequenceEqual(["list", "sink-inputs"]),
            listing
        );
        ScriptServerCookies(runner, cookieBeforeListing, cookieAfterListing);
        var errorLog = new Mock<IErrorLogService>();
        var service = new AudioDuckingService(runner, errorLog.Object);

        service.DuckAudio(0.5f);
        service.RestoreAudio();

        Assert.DoesNotContain(
            runner.Invocations,
            invocation => invocation.Args[0] == "set-sink-input-volume"
        );
        errorLog.VerifyNoOtherCalls();

        // Nothing was saved, so the next dictation ducks as usual.
        service.DuckAudio(0.5f);
        Assert.Equal(1, CountInvocations(runner, duck));
    }

    // Each cookie answers one `pactl info` call in order, null failing that call. Calls
    // past the script report ServerCookie.
    private static void ScriptServerCookies(FakeProcessRunner runner, params string?[] cookies)
    {
        string[] info = ["info"];
        var infoCalls = 0;
        for (var index = 0; index < cookies.Length; index++)
        {
            var call = index;
            Func<string, IReadOnlyList<string>, bool> isThisCall = (_, args) =>
            {
                if (!args.SequenceEqual(info) || infoCalls != call)
                {
                    return false;
                }

                infoCalls++;
                return true;
            };
            if (cookies[index] is { } cookie)
            {
                runner.RespondWith(isThisCall, $"Cookie: {cookie}\n");
            }
            else
            {
                runner.FailWhen(isThisCall, "Connection failure: Connection refused");
            }
        }

        runner.RespondWith(
            (_, args) => args.SequenceEqual(info),
            $"Server Name: pulseaudio\nCookie: {ServerCookie}\n"
        );
    }

    private static int CountInvocations(FakeProcessRunner runner, IReadOnlyList<string> args)
    {
        return runner.Invocations.Count(invocation => invocation.Args.SequenceEqual(args));
    }
}
