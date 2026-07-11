using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LearnedCorrectionsFeedbackPresenterTests
{
    // Captures the presenter's scheduled auto-hide so a test can fire it deterministically
    // instead of waiting real seconds. Re-arming disposes the prior handle (Fired flips false),
    // mirroring how a re-armed DispatcherTimer supersedes the last one.
    private sealed class FakeScheduler
    {
        private ScheduledDelay? _pending;

        public TimeSpan? LastDelay => _pending?.Delay;

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new ScheduledDelay(delay, callback);
            _pending = entry;
            return entry;
        }

        // Fires the currently pending (un-disposed) delay, if any.
        public void FirePending()
        {
            _pending?.Fire();
        }

        private sealed class ScheduledDelay(TimeSpan delay, Action callback) : IDisposable
        {
            private bool _cancelled;

            public TimeSpan Delay { get; } = delay;

            public void Fire()
            {
                if (!_cancelled)
                {
                    callback();
                }
            }

            public void Dispose()
            {
                _cancelled = true;
            }
        }
    }

    private static (LearnedCorrectionsFeedbackPresenter Presenter, Mock<IDictionaryService> Dictionary,
        FakeScheduler Scheduler, List<LearnedCorrectionsFeedback> Emitted) CreateSut()
    {
        var dictionary = new Mock<IDictionaryService>();
        var scheduler = new FakeScheduler();
        var presenter = new LearnedCorrectionsFeedbackPresenter(dictionary.Object, scheduler.Schedule);
        var emitted = new List<LearnedCorrectionsFeedback>();
        presenter.FeedbackChanged += emitted.Add;
        return (presenter, dictionary, scheduler, emitted);
    }

    private static LearnedDictionaryCorrection Correction(string id, string original, string replacement)
    {
        return new LearnedDictionaryCorrection(id, original, replacement);
    }

    [Fact]
    public void ShowLearned_SingleCorrection_SetsPendingAndFormatsPair()
    {
        var (presenter, _, scheduler, emitted) = CreateSut();

        presenter.ShowLearned([Correction("1", "kubernetes", "Kubernetes")]);

        Assert.True(presenter.HasPendingBatch);
        var feedback = Assert.Single(emitted);
        Assert.True(feedback.ShowUndo);
        Assert.Contains("kubernetes", feedback.Text);
        Assert.Contains("Kubernetes", feedback.Text);
        Assert.Equal(TimeSpan.FromSeconds(8), scheduler.LastDelay);
    }

    [Fact]
    public void ShowLearned_MultipleCorrections_UsesCountFormat()
    {
        var (presenter, _, _, emitted) = CreateSut();

        presenter.ShowLearned(
        [
            Correction("1", "a", "A"),
            Correction("2", "b", "B")
        ]);

        var feedback = Assert.Single(emitted);
        Assert.Contains("2", feedback.Text);
        Assert.True(feedback.ShowUndo);
    }

    [Fact]
    public void ShowLearned_EmptyBatch_IsIgnored()
    {
        var (presenter, _, _, emitted) = CreateSut();

        presenter.ShowLearned([]);

        Assert.False(presenter.HasPendingBatch);
        Assert.Empty(emitted);
    }

    [Fact]
    public void SecondShowLearned_ReplacesPendingBatch()
    {
        var (presenter, dictionary, _, _) = CreateSut();
        var first = new[] { Correction("1", "old", "Old") };
        var second = new[] { Correction("2", "new", "New") };

        presenter.ShowLearned(first);
        presenter.ShowLearned(second);
        presenter.Undo();

        // Undo must act on the SECOND batch only — the first was superseded, not accumulated.
        dictionary.Verify(
            d => d.UndoLearnedCorrections(
                It.Is<IEnumerable<LearnedDictionaryCorrection>>(b => b.SequenceEqual(second))),
            Times.Once);
        dictionary.Verify(
            d => d.UndoLearnedCorrections(
                It.Is<IEnumerable<LearnedDictionaryCorrection>>(b => b.SequenceEqual(first))),
            Times.Never);
    }

    [Fact]
    public void Undo_RemovesExactBatchAndShowsConfirmation()
    {
        var (presenter, dictionary, scheduler, emitted) = CreateSut();
        var batch = new[] { Correction("1", "teh", "the") };
        presenter.ShowLearned(batch);
        emitted.Clear();

        presenter.Undo();

        dictionary.Verify(
            d => d.UndoLearnedCorrections(
                It.Is<IEnumerable<LearnedDictionaryCorrection>>(b => b.SequenceEqual(batch))),
            Times.Once);
        Assert.False(presenter.HasPendingBatch);
        var confirmation = Assert.Single(emitted);
        Assert.False(confirmation.ShowUndo);
        Assert.Equal("Correction learning undone.", confirmation.Text);
        Assert.Equal(TimeSpan.FromSeconds(2), scheduler.LastDelay);
    }

    [Fact]
    public void Undo_WithNothingPending_IsNoOp()
    {
        var (presenter, dictionary, _, emitted) = CreateSut();

        presenter.Undo();

        dictionary.Verify(
            d => d.UndoLearnedCorrections(It.IsAny<IEnumerable<LearnedDictionaryCorrection>>()),
            Times.Never);
        Assert.Empty(emitted);
    }

    [Fact]
    public void AutoHideExpiry_ClearsPendingAndHidesToast()
    {
        var (presenter, dictionary, scheduler, emitted) = CreateSut();
        presenter.ShowLearned([Correction("1", "teh", "the")]);
        emitted.Clear();

        scheduler.FirePending();

        Assert.False(presenter.HasPendingBatch);
        var hidden = Assert.Single(emitted);
        Assert.Equal(string.Empty, hidden.Text);
        Assert.False(hidden.ShowUndo);
        // Expiry must not touch the dictionary — the correction stays learned.
        dictionary.Verify(
            d => d.UndoLearnedCorrections(It.IsAny<IEnumerable<LearnedDictionaryCorrection>>()),
            Times.Never);
    }

    [Fact]
    public void ReArm_CancelsPreviousAutoHide()
    {
        var (presenter, _, scheduler, emitted) = CreateSut();
        presenter.ShowLearned([Correction("1", "a", "A")]);

        // Second show re-arms; the first timer's handle was disposed, so firing "pending"
        // fires only the latest. The stale timer must not hide the fresh toast.
        presenter.ShowLearned([Correction("2", "b", "B")]);
        emitted.Clear();
        scheduler.FirePending();

        var hidden = Assert.Single(emitted);
        Assert.Equal(string.Empty, hidden.Text);
        Assert.False(presenter.HasPendingBatch);
    }

    [Fact]
    public void Reset_ClearsPendingSilentlyWithoutEmitting()
    {
        var (presenter, dictionary, scheduler, emitted) = CreateSut();
        presenter.ShowLearned([Correction("1", "a", "A")]);
        emitted.Clear();

        presenter.Reset();

        Assert.False(presenter.HasPendingBatch);
        Assert.Empty(emitted);

        // The cancelled auto-hide must not fire after a reset.
        scheduler.FirePending();
        Assert.Empty(emitted);

        // And a subsequent undo is inert (nothing pending, dictionary untouched).
        presenter.Undo();
        dictionary.Verify(
            d => d.UndoLearnedCorrections(It.IsAny<IEnumerable<LearnedDictionaryCorrection>>()),
            Times.Never);
    }
}
