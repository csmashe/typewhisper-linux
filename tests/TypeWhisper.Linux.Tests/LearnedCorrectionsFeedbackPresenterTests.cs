using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LearnedCorrectionsFeedbackPresenterTests
{
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
        // Catalog, not the English copy: still fails on a wrong key (which resolves to itself)
        // without breaking on a wording edit.
        Assert.Equal(Loc.Instance["Feedback.CorrectionLearningUndone"], confirmation.Text);
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
    public void StaleAutoHide_AfterReArm_LeavesFreshBatchVisible()
    {
        // FakeScheduler models disposal as cancellation, so it can't reproduce the real hazard: in
        // production the superseded callback is already queued when the re-arm disposes its handle,
        // and disposal can't retract it. Invoke the raw callbacks to exercise _feedbackGeneration.
        var dictionary = new Mock<IDictionaryService>();
        var callbacks = new List<Action>();
        var presenter = new LearnedCorrectionsFeedbackPresenter(
            dictionary.Object,
            (_, callback) =>
            {
                callbacks.Add(callback);
                return new NoopHandle();
            });
        var emitted = new List<LearnedCorrectionsFeedback>();
        presenter.FeedbackChanged += emitted.Add;

        presenter.ShowLearned([Correction("1", "a", "A")]);
        presenter.ShowLearned([Correction("2", "b", "B")]);
        emitted.Clear();

        callbacks[0]();

        // The stale hide belongs to the superseded generation: the fresh batch and its Undo stay.
        Assert.True(presenter.HasPendingBatch);
        Assert.Empty(emitted);

        callbacks[1]();
        Assert.False(presenter.HasPendingBatch);
        Assert.Equal(string.Empty, Assert.Single(emitted).Text);
    }

    private sealed class NoopHandle : IDisposable
    {
        public void Dispose()
        {
        }
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
