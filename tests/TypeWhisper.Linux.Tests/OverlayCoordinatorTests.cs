using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class OverlayCoordinatorTests
{
    [Fact]
    public void ForeignStaleAndReleasedTokens_AreNoOps()
    {
        var sut = CreateCoordinator();
        var other = CreateCoordinator();
        var token = sut.Acquire(OverlayRequester.Dictation);
        var foreign = other.Acquire(OverlayRequester.Dictation);
        Assert.True(sut.Show(token, Processing("owned")));

        AssertRejected(sut, foreign);

        var stale = token;
        token = sut.Acquire(OverlayRequester.Dictation);
        AssertRejected(sut, stale);

        Assert.True(sut.Show(token, Processing("replacement")));
        Assert.True(sut.Release(token));
        AssertRejected(sut, token);
    }

    [Fact]
    public void ReAcquireSameRequester_InvalidatesOldTokenAndNewTokenPresents()
    {
        var sut = CreateCoordinator();
        var oldToken = sut.Acquire(OverlayRequester.Dictation);
        Assert.True(sut.Show(oldToken, Recording("old")));

        var newToken = sut.Acquire(OverlayRequester.Dictation);

        Assert.Equal(DictationOverlayState.Hidden, sut.PresentedState);
        Assert.False(sut.Show(oldToken, Recording("stale")));
        Assert.True(sut.Show(newToken, Recording("new")));
        Assert.Equal("new", sut.PresentedState.StatusText);
    }

    [Fact]
    public void Recording_BeatsProcessingAndFallsBackWhenRecordingReleases()
    {
        var sut = CreateCoordinator();
        // The processing claim is deliberately OLDER: on the equal-priority tie-break the
        // earlier claim would win, so recording taking the presentation proves priority.
        var processing = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(processing, Processing("processing")));
        var recording = sut.Acquire(OverlayRequester.Dictation);
        Assert.True(sut.Show(recording, Recording("recording")));

        Assert.Equal("recording", sut.PresentedState.StatusText);

        Assert.True(sut.Show(processing, Processing("processing 2")));

        Assert.Equal("recording", sut.PresentedState.StatusText);
        Assert.True(sut.Release(recording));
        Assert.Equal("processing 2", sut.PresentedState.StatusText);
    }

    [Fact]
    public void Recording_BeatsTerminalAndTransientFeedback()
    {
        var scheduler = new ManualScheduler();
        var sut = CreateCoordinator(scheduler);
        // The competing claim is deliberately OLDER so no outcome below is explainable
        // by the claim-age tie-break alone.
        var other = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(other, Processing("other processing")));
        var recording = sut.Acquire(OverlayRequester.Dictation);
        Assert.True(sut.Show(recording, Recording("recording")));
        Assert.Equal("recording", sut.PresentedState.StatusText);

        Assert.True(sut.Show(other, Feedback("terminal")));
        Assert.Equal("recording", sut.PresentedState.StatusText);

        other = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(other, Feedback("transient")));
        Assert.Equal("recording", sut.PresentedState.StatusText);

        Assert.True(sut.Release(recording));
        Assert.Equal("transient", sut.PresentedState.FeedbackText);
    }

    [Fact]
    public void EqualPriority_EarlierClaimIsStableAndItsOwnUpdatesRender()
    {
        var presentations = new List<OverlayPresentationChangedEventArgs>();
        var sut = CreateCoordinator();
        sut.PresentationChanged += (_, presentation) => presentations.Add(presentation);
        var earlier = sut.Acquire(OverlayRequester.Dictation);
        var later = sut.Acquire(OverlayRequester.Transform);

        Assert.True(sut.Show(earlier, Processing("earlier 1")));
        Assert.True(sut.Show(later, Processing("later 1")));
        Assert.True(sut.Update(later, state => state with { StatusText = "later 2" }));

        Assert.Equal("earlier 1", sut.PresentedState.StatusText);
        Assert.Single(presentations);

        Assert.True(sut.Update(earlier, state => state with { StatusText = "earlier 2" }));

        Assert.Equal("earlier 2", sut.PresentedState.StatusText);
        Assert.Equal(2, presentations.Count);
    }

    [Fact]
    public void SuppressedTerminalFeedback_IsDiscardedAndNeverResurfaces()
    {
        var scheduler = new ManualScheduler();
        var sut = CreateCoordinator(scheduler);
        var recording = sut.Acquire(OverlayRequester.Dictation);
        var terminal = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(recording, Recording("recording")));
        Assert.True(sut.Show(terminal, Processing("processing")));

        Assert.True(sut.Show(terminal, Feedback("finished")));
        Assert.True(sut.Release(recording));

        Assert.Equal(DictationOverlayState.Hidden, sut.PresentedState);
        Assert.False(scheduler.HasLiveEntries);
    }

    [Fact]
    public void PresentedFeedbackExpiry_FallsBackToOtherLiveTokenThenHidden()
    {
        var scheduler = new ManualScheduler();
        var sut = CreateCoordinator(scheduler);
        var terminal = sut.Acquire(OverlayRequester.Dictation);
        var transient = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(terminal, Processing("processing")));
        Assert.True(sut.Show(terminal, Feedback("terminal")));
        Assert.True(sut.Show(transient, Feedback("transient")));
        Assert.Equal("terminal", sut.PresentedState.FeedbackText);

        scheduler.Fire(0);

        Assert.Equal("transient", sut.PresentedState.FeedbackText);

        scheduler.Fire(1);

        Assert.Equal(DictationOverlayState.Hidden, sut.PresentedState);
    }

    [Fact]
    public void SuppressedFeedbackExpiry_DoesNotTouchPresentation()
    {
        var scheduler = new ManualScheduler();
        var presentations = new List<OverlayPresentationChangedEventArgs>();
        var sut = CreateCoordinator(scheduler);
        sut.PresentationChanged += (_, presentation) => presentations.Add(presentation);
        var recording = sut.Acquire(OverlayRequester.Dictation);
        var transient = sut.Acquire(OverlayRequester.Transform);
        Assert.True(sut.Show(recording, Recording("recording")));
        Assert.True(sut.Show(transient, Feedback("transient")));
        var revision = sut.Revision;
        var presentationCount = presentations.Count;

        scheduler.Fire(0);

        Assert.Equal("recording", sut.PresentedState.StatusText);
        Assert.Equal(revision, sut.Revision);
        Assert.Equal(presentationCount, presentations.Count);
    }

    [Fact]
    public void OutOfOrderDelivery_ViewModelIgnoresOlderRevision()
    {
        var posts = new List<Action>();
        var settings = new FakeSettingsService(AppSettings.Default);
        var sut = new OverlayCoordinator(settings, posts.Add);
        var viewModel = new DictationOverlayViewModel(
            settings,
            static action => action(),
            overlayCoordinator: sut
        );
        var token = sut.Acquire(OverlayRequester.Dictation);
        Assert.True(sut.Show(token, Processing("older")));
        Assert.True(sut.Update(token, state => state with { StatusText = "newer" }));

        Assert.Equal(2, posts.Count);
        posts[1]();
        posts[0]();

        Assert.True(viewModel.IsOverlayVisible);
        Assert.Equal("newer", viewModel.StatusText);
    }

    [Fact]
    public void PublishedPresentationRevisions_AreStrictlyMonotonic()
    {
        var revisions = new List<long>();
        var sut = CreateCoordinator();
        sut.PresentationChanged += (_, presentation) =>
            revisions.Add(presentation.Revision);
        var token = sut.Acquire(OverlayRequester.Dictation);

        Assert.True(sut.Show(token, Processing("one")));
        Assert.True(sut.Update(token, state => state with { StatusText = "two" }));
        Assert.True(sut.Hide(token));

        Assert.Equal([1L, 2L, 3L], revisions);
    }

    private static OverlayCoordinator CreateCoordinator(ManualScheduler? scheduler = null)
    {
        var settings = new FakeSettingsService(
            AppSettings.Default with { PreviewBubbleAutoHideMilliseconds = 1500 }
        );
        return new OverlayCoordinator(
            settings,
            static action => action(),
            scheduler is null ? null : scheduler.Schedule
        );
    }

    private static void AssertRejected(
        OverlayCoordinator sut,
        OverlayPresentationToken token
    )
    {
        var state = sut.PresentedState;
        var revision = sut.Revision;
        var updaterInvoked = false;

        Assert.False(sut.Show(token, Recording("rejected")));
        Assert.False(sut.Update(token, current =>
        {
            updaterInvoked = true;
            return current with { StatusText = "rejected" };
        }));
        Assert.False(sut.Hide(token));
        Assert.False(sut.Release(token));
        Assert.False(updaterInvoked);
        Assert.Equal(state, sut.PresentedState);
        Assert.Equal(revision, sut.Revision);
    }

    private static DictationOverlayState Recording(string status) =>
        new()
        {
            IsOverlayVisible = true,
            IsRecording = true,
            StatusText = status,
        };

    private static DictationOverlayState Processing(string status) =>
        new()
        {
            IsOverlayVisible = true,
            StatusText = status,
        };

    private static DictationOverlayState Feedback(string text) =>
        new()
        {
            ShowFeedback = true,
            FeedbackText = text,
            StatusText = text,
        };

    private sealed class ManualScheduler
    {
        private readonly List<ScheduledEntry> _entries = [];

        public bool HasLiveEntries => _entries.Any(entry => !entry.IsCancelled);

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new ScheduledEntry(callback);
            _entries.Add(entry);
            return entry;
        }

        public void Fire(int index)
        {
            _entries[index].Fire();
        }

        private sealed class ScheduledEntry(Action callback) : IDisposable
        {
            public bool IsCancelled { get; private set; }

            public void Fire()
            {
                if (!IsCancelled)
                {
                    callback();
                }
            }

            public void Dispose()
            {
                IsCancelled = true;
            }
        }
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load() => Current;

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

        private void Change(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
