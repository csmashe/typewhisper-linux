using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

// The assertion helpers and Assert.All/Collection lambdas in this file assert on
// their parameters; ReSharper reads xUnit asserts as precondition checks and
// concludes the parameters are only validated, never used — but asserting on them
// is exactly the test's purpose, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
namespace TypeWhisper.Linux.Tests;

public sealed class RecordingNotificationServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Recording_processing_and_success_replace_one_notification_in_place()
    {
        const int terminalExpiry = 2345;
        var (source, runner, service) = CreateSut(
            AppSettings.Default with
            {
                Mode = RecordingMode.PushToTalk,
                PreviewBubbleAutoHideMilliseconds = terminalExpiry,
            }
        );
        service.Initialize();

        source.Raise(
            new DictationOverlayState
            {
                IsOverlayVisible = true,
                IsRecording = true,
                StatusText = Loc.Instance["Dictation.StatusRecording"],
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        var processing = Loc.Instance["Overlay.Processing"];
        source.Raise(
            new DictationOverlayState
            {
                IsOverlayVisible = true,
                StatusText = processing,
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        const string success = "Inserted 18 characters";
        source.Raise(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = success,
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        var calls = AssertNotifyCalls(runner, 3);
        AssertNotify(
            calls[0],
            replacesId: 0,
            Loc.Instance["Appearance.NotificationRecordingTitle"],
            RecordingNotificationService.BodyFor(RecordingMode.PushToTalk),
            expireTimeout: 0
        );
        AssertNotify(calls[1], replacesId: 41, processing, string.Empty, expireTimeout: 0);
        AssertNotify(
            calls[2],
            replacesId: 41,
            success,
            string.Empty,
            expireTimeout: terminalExpiry
        );
        Assert.DoesNotContain(runner.Invocations, IsClose);
    }

    [Theory]
    [InlineData("Dictation completed", false)]
    [InlineData("Overlay.Canceled", false)]
    [InlineData("Overlay.NoSpeech", true)]
    public async Task Terminal_variants_show_exact_feedback_with_normalized_finite_expiry(
        string textOrLocalizationKey,
        bool isError
    )
    {
        var settings = AppSettings.Default with
        {
            PreviewBubbleAutoHideMilliseconds =
                AppSettings.MaxPreviewBubbleAutoHideMilliseconds + 500,
        };
        var (source, runner, service) = CreateSut(settings);
        service.Initialize();
        var feedbackText = textOrLocalizationKey.StartsWith("Overlay.", StringComparison.Ordinal)
            ? Loc.Instance[textOrLocalizationKey]
            : textOrLocalizationKey;

        source.Raise(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackIsError = isError,
                FeedbackText = feedbackText,
                IsRecording = false,
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        var call = Assert.Single(AssertNotifyCalls(runner, 1));
        AssertNotify(
            call,
            replacesId: 0,
            feedbackText,
            string.Empty,
            AppSettings.MaxPreviewBubbleAutoHideMilliseconds
        );
    }

    [Fact]
    public async Task Non_presentation_changes_are_deduplicated_while_recording_and_processing()
    {
        var (source, runner, service) = CreateSut(AppSettings.Default);
        service.Initialize();
        var recording = new DictationOverlayState
        {
            IsOverlayVisible = true,
            IsRecording = true,
            PartialText = "one",
            ActiveProfileName = "Profile A",
            ActiveAppName = "Editor",
        };

        source.Raise(recording);
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);
        source.Raise(
            recording with
            {
                PartialText = "one two",
                ActiveProfileName = "Profile B",
                ActiveAppName = "Terminal",
                SessionStartedAtUtc = DateTime.UtcNow,
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        var processing = new DictationOverlayState
        {
            IsOverlayVisible = true,
            StatusText = Loc.Instance["Overlay.Processing"],
        };
        source.Raise(processing);
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);
        source.Raise(
            processing with
            {
                PartialText = "ignored preview",
                ActiveProfileName = "Profile C",
                ActiveAppName = "Browser",
            }
        );
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        var calls = AssertNotifyCalls(runner, 2);
        Assert.Equal(Loc.Instance["Appearance.NotificationRecordingTitle"], calls[0].Args[11]);
        Assert.Equal(Loc.Instance["Overlay.Processing"], calls[1].Args[11]);
    }

    [Fact]
    public async Task Hidden_and_zero_duration_terminal_feedback_close_the_owned_notification()
    {
        var (hiddenSource, hiddenRunner, hiddenService) = CreateSut(AppSettings.Default);
        hiddenService.Initialize();
        hiddenSource.Raise(new DictationOverlayState { IsRecording = true });
        await hiddenService.WaitForIdleAsync().WaitAsync(s_testGuard);

        hiddenSource.Raise(DictationOverlayState.Hidden);
        await hiddenService.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Equal(2, hiddenRunner.Invocations.Count);
        AssertNotify(hiddenRunner.Invocations[0], 0, Loc.Instance["Appearance.NotificationRecordingTitle"], RecordingNotificationService.BodyFor(AppSettings.Default.Mode), 0);
        AssertClose(hiddenRunner.Invocations[1], 41);

        var zeroSettings = AppSettings.Default with
        {
            PreviewBubbleAutoHideMilliseconds = -100,
        };
        var (zeroSource, zeroRunner, zeroService) = CreateSut(zeroSettings);
        zeroService.Initialize();
        zeroSource.Raise(new DictationOverlayState { IsRecording = true });
        await zeroService.WaitForIdleAsync().WaitAsync(s_testGuard);

        zeroSource.Raise(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Finished",
            }
        );
        await zeroService.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Equal(2, zeroRunner.Invocations.Count);
        AssertNotify(zeroRunner.Invocations[0], 0, Loc.Instance["Appearance.NotificationRecordingTitle"], RecordingNotificationService.BodyFor(AppSettings.Default.Mode), 0);
        AssertClose(zeroRunner.Invocations[1], 41);
    }

    [Fact]
    public async Task Slow_initial_notify_coalesces_pending_states_to_latest_terminal_feedback()
    {
        var source = new FakeOverlayStateSource();
        var runner = new ControlledProcessRunner();
        var settings = CreateSettings(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1700 }
        );
        var service = new RecordingNotificationService(source, settings.Object, runner, true);
        service.Initialize();

        source.Raise(new DictationOverlayState { IsRecording = true });
        await runner.FirstStarted.Task.WaitAsync(s_testGuard);

        source.Raise(
            new DictationOverlayState
            {
                IsOverlayVisible = true,
                StatusText = Loc.Instance["Overlay.Processing"],
            }
        );
        const string terminal = "Dictation inserted";
        source.Raise(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = terminal,
            }
        );
        Assert.Single(runner.Invocations);

        runner.CompleteFirst(41);
        await runner.SecondStarted.Task.WaitAsync(s_testGuard);

        var second = runner.Invocations[1];
        AssertNotify(second, replacesId: 41, terminal, string.Empty, expireTimeout: 1700);
        runner.CompleteSecond(41);
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Equal(2, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Equal("gdbus", invocation.FileName));
        Assert.DoesNotContain(runner.Invocations, IsClose);
    }

    [Fact]
    public async Task Disabled_service_does_not_subscribe_dispatch_or_close_on_dispose()
    {
        var source = new FakeOverlayStateSource();
        var runner = new FakeProcessRunner();
        var service = new RecordingNotificationService(
            source,
            CreateSettings(AppSettings.Default).Object,
            runner,
            false
        );

        service.Initialize();
        source.Raise(new DictationOverlayState { IsRecording = true });
        service.Dispose();
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Enabled_dispose_closes_owned_notification_and_ignores_later_states()
    {
        var (source, runner, service) = CreateSut(AppSettings.Default);
        service.Initialize();
        source.Raise(new DictationOverlayState { IsRecording = true });
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);

        service.Dispose();
        await service.WaitForIdleAsync().WaitAsync(s_testGuard);
        source.Raise(
            new DictationOverlayState
            {
                IsOverlayVisible = true,
                StatusText = Loc.Instance["Overlay.Processing"],
            }
        );

        Assert.Equal(2, runner.Invocations.Count);
        AssertClose(runner.Invocations[1], 41);
    }

    [Fact]
    public async Task Failed_and_invalid_notify_results_do_not_retry_without_a_new_presentation()
    {
        var failedSource = new FakeOverlayStateSource();
        var failedRunner = new FakeProcessRunner();
        failedRunner.FailWhen(IsNotify);
        var failedService = new RecordingNotificationService(
            failedSource,
            CreateSettings(AppSettings.Default).Object,
            failedRunner,
            true
        );
        failedService.Initialize();
        var recording = new DictationOverlayState { IsRecording = true };

        failedSource.Raise(recording);
        await failedService.WaitForIdleAsync().WaitAsync(s_testGuard);
        failedSource.Raise(recording with { PartialText = "noise" });
        await failedService.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Single(failedRunner.Invocations);

        var invalidSource = new FakeOverlayStateSource();
        var invalidRunner = new FakeProcessRunner();
        invalidRunner.RespondWith(IsNotify, "not a notification id");
        var invalidService = new RecordingNotificationService(
            invalidSource,
            CreateSettings(AppSettings.Default).Object,
            invalidRunner,
            true
        );
        invalidService.Initialize();

        invalidSource.Raise(recording);
        await invalidService.WaitForIdleAsync().WaitAsync(s_testGuard);
        invalidSource.Raise(recording with { ActiveAppName = "noise" });
        await invalidService.WaitForIdleAsync().WaitAsync(s_testGuard);

        Assert.Single(invalidRunner.Invocations);
    }

    private static (
        FakeOverlayStateSource Source,
        FakeProcessRunner Runner,
        RecordingNotificationService Service
        ) CreateSut(AppSettings current)
    {
        var source = new FakeOverlayStateSource();
        var runner = new FakeProcessRunner();
        runner.RespondWith(IsNotify, "(uint32 41,)");
        var service = new RecordingNotificationService(
            source,
            CreateSettings(current).Object,
            runner,
            true
        );
        return (source, runner, service);
    }

    private static Mock<ISettingsService> CreateSettings(AppSettings current)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(current);
        return settings;
    }

    private static List<FakeProcessRunner.Invocation> AssertNotifyCalls(
        FakeProcessRunner runner,
        int expectedCount
    )
    {
        Assert.Equal(expectedCount, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation =>
        {
            Assert.Equal("gdbus", invocation.FileName);
            Assert.Equal("org.freedesktop.Notifications.Notify", invocation.Args[7]);
            Assert.Equal(TimeSpan.FromSeconds(3), invocation.Timeout);
        });
        return runner.Invocations;
    }

    private static void AssertNotify(
        FakeProcessRunner.Invocation invocation,
        uint replacesId,
        string summary,
        string body,
        int expireTimeout
    )
    {
        AssertNotify(invocation.FileName, invocation.Args, invocation.Timeout, replacesId, summary, body, expireTimeout);
    }

    private static void AssertNotify(
        ControlledProcessRunner.Invocation invocation,
        uint replacesId,
        string summary,
        string body,
        int expireTimeout
    )
    {
        AssertNotify(invocation.FileName, invocation.Args, invocation.Timeout, replacesId, summary, body, expireTimeout);
    }

    private static void AssertNotify(
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan? timeout,
        uint replacesId,
        string summary,
        string body,
        int expireTimeout
    )
    {
        Assert.Equal("gdbus", fileName);
        Assert.Equal("org.freedesktop.Notifications.Notify", args[7]);
        Assert.Equal("TypeWhisper", args[8]);
        Assert.Equal(replacesId.ToString(), args[9]);
        Assert.Equal(summary, args[11]);
        Assert.Equal(body, args[12]);
        Assert.Equal("[]", args[13]);
        Assert.Equal("{}", args[14]);
        Assert.Equal(expireTimeout.ToString(), args[15]);
        Assert.Equal(TimeSpan.FromSeconds(3), timeout);
    }

    private static void AssertClose(FakeProcessRunner.Invocation invocation, uint id)
    {
        Assert.Equal("gdbus", invocation.FileName);
        Assert.Equal("org.freedesktop.Notifications.CloseNotification", invocation.Args[7]);
        Assert.Equal(id.ToString(), invocation.Args[8]);
        Assert.Equal(TimeSpan.FromSeconds(3), invocation.Timeout);
    }

    private static bool IsNotify(string fileName, IReadOnlyList<string> args)
    {
        return fileName == "gdbus"
               && args.Count > 7
               && args[7] == "org.freedesktop.Notifications.Notify";
    }

    private static bool IsClose(FakeProcessRunner.Invocation invocation)
    {
        return invocation.Args.Count > 7
               && invocation.Args[7] == "org.freedesktop.Notifications.CloseNotification";
    }

    private static bool IsClose(ControlledProcessRunner.Invocation invocation)
    {
        return invocation.Args.Count > 7
               && invocation.Args[7] == "org.freedesktop.Notifications.CloseNotification";
    }

    private sealed class FakeOverlayStateSource : IRecordingNotificationStateSource
    {
        public event EventHandler<DictationOverlayState>? OverlayStateChanged;

        public void Raise(DictationOverlayState state)
        {
            OverlayStateChanged?.Invoke(this, state);
        }
    }

    private sealed class ControlledProcessRunner : IProcessRunner
    {
        private readonly TaskCompletionSource<ProcessRunResult> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ProcessRunResult> _secondCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Invocation> Invocations { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default
        )
        {
            Invocations.Add(new Invocation(fileName, args.ToArray(), timeout));
            switch (Invocations.Count)
            {
                case 1:
                    FirstStarted.TrySetResult();
                    return _firstCompletion.Task;
                case 2:
                    SecondStarted.TrySetResult();
                    return _secondCompletion.Task;
                default:
                    return Task.FromResult(Success(41));
            }
        }

        public void CompleteFirst(uint id)
        {
            _firstCompletion.TrySetResult(Success(id));
        }

        public void CompleteSecond(uint id)
        {
            _secondCompletion.TrySetResult(Success(id));
        }

        private static ProcessRunResult Success(uint id)
        {
            return new ProcessRunResult(
                true,
                false,
                0,
                $"(uint32 {id},)",
                string.Empty
            );
        }

        public sealed record Invocation(
            string FileName,
            IReadOnlyList<string> Args,
            TimeSpan? Timeout
        );
    }
}
