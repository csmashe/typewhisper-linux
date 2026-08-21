using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOverlayViewModelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingTimerTick_TimerInSideSlot_RaisesOnlyCorrespondingTextNotification(
        bool timerOnLeft
    )
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = timerOnLeft
                    ? OverlayWidget.Timer
                    : OverlayWidget.Profile,
                OverlayRightWidget = timerOnLeft
                    ? OverlayWidget.Profile
                    : OverlayWidget.Timer,
            });
        var sut = CreateViewModel(settings);
        var propertyNames = TrackPropertyChanges(sut);

        sut.RecordingTimerTick();

        Assert.Equal(
            [timerOnLeft
                ? nameof(DictationOverlayViewModel.LeftText)
                : nameof(DictationOverlayViewModel.RightText)],
            SideTextNotifications(propertyNames)
        );
    }

    [Fact]
    public void RecordingTimerTick_NoTimerSlots_DoesNotRaiseSideTextNotifications()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
                OverlayRightWidget = OverlayWidget.HotkeyMode,
            });
        var sut = CreateViewModel(settings);
        var propertyNames = TrackPropertyChanges(sut);

        sut.RecordingTimerTick();

        Assert.Empty(SideTextNotifications(propertyNames));
    }

    [Fact]
    public void ClockTimerTick_VisibleClockSlot_RaisesOnlyClockSlotTextNotification()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
                OverlayRightWidget = OverlayWidget.Clock,
            });
        var sut = CreateViewModel(settings);
        sut.IsOverlayVisible = true;
        var propertyNames = TrackPropertyChanges(sut);

        sut.ClockTimerTick();

        Assert.Equal(
            [nameof(DictationOverlayViewModel.RightText)],
            SideTextNotifications(propertyNames)
        );

        sut.IsOverlayVisible = false;
    }

    [Fact]
    public void ClockTimer_TracksLiveSlotSettingAndOverlayVisibility()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                OverlayLeftWidget = OverlayWidget.Clock,
                OverlayRightWidget = OverlayWidget.Profile,
            });
        var sut = CreateViewModel(settings);

        Assert.False(sut.IsClockTimerRunning);

        sut.IsOverlayVisible = true;

        Assert.True(sut.IsClockTimerRunning);

        settings.Change(
            settings.Current with
            {
                OverlayLeftWidget = OverlayWidget.Profile,
            });

        Assert.False(sut.IsClockTimerRunning);

        settings.Change(
            settings.Current with
            {
                OverlayRightWidget = OverlayWidget.Clock,
            });

        Assert.True(sut.IsClockTimerRunning);

        sut.IsOverlayVisible = false;

        Assert.False(sut.IsClockTimerRunning);
    }

    [Fact]
    public void AudioLevel_ServiceDeliveryUpdatesBeforeJobReturnsWithoutConsumerPost()
    {
        var serviceJobs = new List<Action>();
        var consumerPostCount = 0;
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            postToUiThread: serviceJobs.Add
        );
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            _ => consumerPostCount++,
            audio
        )
        {
            IsRecording = true,
        };
        Assert.True(audio.StartPreview());

        audio.ProcessAudioBufferForTest([0.1f]);
        var serviceDelivery = Assert.Single(serviceJobs);

        serviceDelivery();

        Assert.Equal(0, consumerPostCount);
        Assert.Equal(0.8, sut.AudioLevel, precision: 5);
    }

    [Fact]
    public void AudioLevel_StoppedOverlayIgnoresLaterServiceDelivery()
    {
        var serviceJobs = new List<Action>();
        var consumerPostCount = 0;
        using var audio = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            postToUiThread: serviceJobs.Add
        );
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            action =>
            {
                consumerPostCount++;
                action();
            },
            audio
        );
        Assert.True(audio.StartPreview());

        audio.ProcessAudioBufferForTest([0.1f]);
        Assert.Single(serviceJobs)();

        Assert.Equal(0, consumerPostCount);
        Assert.Equal(0, sut.AudioLevel);
    }

    [Fact]
    public void Action_result_uses_plugin_duration_override_and_offers_url()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1200 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.True(sut.ShowFeedback);
        Assert.True(sut.HasActionResultUrl);
        Assert.Equal("https://example.com/issues/42", sut.ActionResultUrl);
        Assert.Equal(TimeSpan.FromSeconds(5), sut.FeedbackTimerInterval);
    }

    [Fact]
    public void Global_zero_hides_action_feedback_even_with_plugin_duration()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 0 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.False(sut.ShowFeedback);
        Assert.False(sut.HasActionResultUrl);
        Assert.Null(sut.ActionResultUrl);
    }

    [Fact]
    public void OpenActionResultCommand_never_opens_until_explicitly_invoked()
    {
        var openedUrls = new List<string?>();
        var sut = new DictationOverlayViewModel(
            new FakeSettingsService(AppSettings.Default),
            static action => action(),
            openUrl: url =>
            {
                openedUrls.Add(url);
                return true;
            }
        );

        sut.ApplyState(
            new DictationOverlayState
            {
                ShowFeedback = true,
                FeedbackText = "Issue created",
                ActionResultUrl = "https://example.com/issues/42",
                FeedbackDurationMilliseconds = 5000,
            }
        );

        Assert.Empty(openedUrls);

        sut.OpenActionResultCommand.Execute(null);

        Assert.Equal(["https://example.com/issues/42"], openedUrls);
    }

    [Fact]
    public void CoordinatorPresentation_RendersPropertiesWithoutViewModelExpiryTimer()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var scheduler = new FakeScheduler();
        var coordinator = new OverlayCoordinator(
            settings,
            static action => action(),
            scheduler.Schedule
        );
        var sut = new DictationOverlayViewModel(
            settings,
            static action => action(),
            overlayCoordinator: coordinator
        );
        var token = coordinator.Acquire(OverlayRequester.Dictation);

        Assert.True(coordinator.Show(token, new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Coordinator feedback",
            FeedbackIsError = true,
            FeedbackDurationMilliseconds = 5000,
        }));

        Assert.True(sut.ShowFeedback);
        Assert.Equal("Coordinator feedback", sut.FeedbackText);
        Assert.True(sut.FeedbackIsError);
        Assert.True(sut.HasVisibleContent);
        Assert.False(sut.IsFeedbackTimerRunning);
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.LastDelay);
    }

    [Fact]
    public void LegacyApplyState_RemainsFunctionalAndOwnsFeedbackExpiry()
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        var sut = CreateViewModel(settings);

        sut.ApplyState(new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Legacy feedback",
        });

        Assert.True(sut.ShowFeedback);
        Assert.Equal("Legacy feedback", sut.FeedbackText);
        Assert.True(sut.IsFeedbackTimerRunning);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), sut.FeedbackTimerInterval);
    }

    [Fact]
    public void LegacyProducerEvents_DoNotReplaceCoordinatorPresentation()
    {
        var settings = new FakeSettingsService(AppSettings.Default);
        var coordinator = new OverlayCoordinator(settings, static action => action());
        var dictation = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(
            typeof(DictationOrchestrator)
        );
        var transform = (TransformSelectionService)RuntimeHelpers.GetUninitializedObject(
            typeof(TransformSelectionService)
        );
        dictation.OverlayStateChanged += static (_, _) => { };
        transform.OverlayStateChanged += static (_, _) => { };
        using var audio = new AudioRecordingService(_ => { }, () => 0, () => { });
        var sut = new DictationOverlayViewModel(
            audio,
            settings,
            new Mock<IDetectionFailureTracker>().Object,
            new UrlLauncher(new Mock<IProcessRunner>().Object),
            coordinator
        );
        var token = coordinator.Acquire(OverlayRequester.Dictation);
        Assert.True(coordinator.Show(token, new DictationOverlayState
        {
            IsOverlayVisible = true,
            IsRecording = true,
            StatusText = "Coordinator recording",
        }));

        RaiseLegacyEvent(dictation, new DictationOverlayState
        {
            ShowFeedback = true,
            FeedbackText = "Legacy dictation",
        });
        RaiseLegacyEvent(transform, DictationOverlayState.Hidden);

        Assert.True(sut.IsOverlayVisible);
        Assert.True(sut.IsRecording);
        Assert.False(sut.ShowFeedback);
        Assert.Equal("Coordinator recording", sut.StatusText);
    }

    private static DictationOverlayViewModel CreateViewModel(FakeSettingsService settings)
    {
        return new DictationOverlayViewModel(settings, static action => action());
    }

    private static List<string?> TrackPropertyChanges(DictationOverlayViewModel sut)
    {
        var propertyNames = new List<string?>();
        sut.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);
        return propertyNames;
    }

    private static IEnumerable<string?> SideTextNotifications(IEnumerable<string?> propertyNames)
    {
        return propertyNames.Where(name =>
            name is nameof(DictationOverlayViewModel.LeftText)
                or nameof(DictationOverlayViewModel.RightText));
    }

    private static void RaiseLegacyEvent(object source, DictationOverlayState state)
    {
        var field = source.GetType().GetField(
            nameof(DictationOrchestrator.OverlayStateChanged),
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new MissingFieldException(
            source.GetType().FullName,
            nameof(DictationOrchestrator.OverlayStateChanged)
        );
        var handlers = (EventHandler<DictationOverlayState>?)field.GetValue(source);
        handlers?.Invoke(source, state);
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            Change(settings);
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            var updated = mutate(Current);
            Change(updated);
            return updated;
        }

        public void Change(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
