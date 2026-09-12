using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationRecoveryTests
{
    [Fact]
    public Task UnavailablePrompt_RoutesFailureAndRetryPreservesRecord() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "stale-profile", Name = "Stale profile", PromptActionId = "removed-prompt",
        });
        fixture.Plugin.EnqueueText("preserved transcript");
        await DictateAsync(fixture, "stale-profile");
        var record = Assert.Single(fixture.History.Records);
        Assert.Equal(TextInsertionStatus.ActionUnavailable, record.InsertionStatus);
        Assert.Equal("Configured prompt action is unavailable.", record.InsertionFailureReason);
        Assert.Equal("preserved transcript", record.RawText);
        Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
        Assert.Empty(fixture.InsertionPlatform.Typed);
        fixture.Plugin.EnqueueText("retry transcript");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.RetryFromHistoryAsync(record.Id));
        Assert.Equal(Loc.Instance["History.RetryPromptActionUnavailable"], error.Message);
        Assert.Equal(record, Assert.Single(fixture.History.Records));
    });

    [Fact]
    public Task RetryUnavailablePrompt_PreservesOriginalStatus() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("original failure");
        await DictateAsync(fixture);
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "stale-profile", Name = "Stale profile", PromptActionId = "removed-prompt",
        });
        var record = Assert.Single(fixture.History.Records) with { ProfileName = "Stale profile" };
        Assert.True(fixture.History.TryReplaceRecord(record));
        fixture.Plugin.EnqueueText("retry transcript");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.RetryFromHistoryAsync(record.Id));
        Assert.Equal(Loc.Instance["History.RetryPromptActionUnavailable"], error.Message);
        Assert.Equal(record, Assert.Single(fixture.History.Records));
    });

    [Fact]
    public Task RetryPromptDisabledDuringTranscription_RecordsProcessingFailure() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        var actions = fixture.Provider.GetRequiredService<IPromptActionService>();
        var action = new PromptAction
        {
            Id = "recovery-prompt", Name = "Recovery prompt", SystemPrompt = "Rewrite the input.",
            ProviderOverride = "plugin:integration.scripted-llm:scripted-llm-model",
        };
        actions.AddAction(action);
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "recovery-profile", Name = "Recovery profile", PromptActionId = action.Id,
        });
        fixture.Llm.Failure = new InvalidOperationException("original processing failure");
        fixture.Plugin.EnqueueText("preserved raw text");
        await DictateAsync(fixture, "recovery-profile");
        var record = Assert.Single(fixture.History.Records);
        Assert.Equal(TranscriptionRecordStatus.ProcessingFailed, record.Status);
        Assert.Equal("preserved raw text", record.FinalText);
        fixture.Llm.Failure = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Plugin.EnqueueResult(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new PluginTranscriptionResult("new raw text", "en", 1);
        });
        var retry = fixture.Orchestrator.RetryFromHistoryAsync(record.Id);
        try
        {
            await BoundedTest.WaitAsync(entered.Task);
            actions.UpdateAction(action with { IsEnabled = false });
        }
        finally
        {
            release.TrySetResult();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => retry);
        Assert.Equal(Loc.Instance["History.RetryPromptActionUnavailable"], error.Message);
        var failed = Assert.Single(fixture.History.Records);
        Assert.Equal(TranscriptionRecordStatus.ProcessingFailed, failed.Status);
        Assert.Equal(error.Message, failed.FailureMessage);
        Assert.Equal("new raw text", failed.RawText);
        Assert.Equal(record.FinalText, failed.FinalText);
    });

    [Fact]
    public Task RecoveryDrain_TimesOutWhileProviderIgnoresCancellation() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("original failure");
        await DictateAsync(fixture);
        var record = Assert.Single(fixture.History.Records);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Plugin.EnqueueResult(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return new PluginTranscriptionResult("recovered", "en", 1);
        });
        using var caller = new CancellationTokenSource();
        var retry = fixture.Orchestrator.RetryFromHistoryAsync(record.Id, caller.Token);
        try
        {
            await BoundedTest.WaitAsync(entered.Task);
            var drain = fixture.Orchestrator.CancelRecoveryAndDrainAsync(TimeSpan.FromMilliseconds(50));
            Assert.False(await BoundedTest.WaitAsync(drain));
            Assert.False(retry.IsCompleted);
        }
        finally
        {
            await caller.CancelAsync();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        }
        Assert.Equal(record, Assert.Single(fixture.History.Records));
        Assert.True(await fixture.Orchestrator.CancelRecoveryAndDrainAsync());
    });

    [Fact]
    public Task RetryProviderFailureAfterCancellation_PreservesRecord() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("original failure");
        await DictateAsync(fixture);
        var record = Assert.Single(fixture.History.Records);
        using var caller = new CancellationTokenSource();
        // ReSharper disable once AccessToDisposedClosure -- the queued result runs inside the awaited retry, before the source disposes.
        fixture.Plugin.EnqueueResult(_ =>
        {
            caller.Cancel();
            throw new PluginRequestException("provider canceled", PluginRequestFailureKind.ServerError);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Orchestrator.RetryFromHistoryAsync(record.Id, caller.Token));
        Assert.Equal(record, Assert.Single(fixture.History.Records));
    });

    [Fact]
    public Task TranscriptionFailure_RetainsCapture_AndRetryReplacesAndCopies() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("provider unavailable");
        await DictateAsync(fixture);
        var failed = Assert.Single(fixture.History.Records);
        Assert.Equal(TranscriptionRecordStatus.TranscriptionFailed, failed.Status);
        Assert.Equal("", failed.RawText);
        Assert.Equal("", failed.FinalText);
        Assert.Equal("provider unavailable", failed.FailureMessage);
        Assert.True(fixture.Provider.GetRequiredService<SessionAudioFileService>().HasAudio(failed.AudioFileName));
        fixture.History.TryReplaceRecord(failed with
        {
            TranscriptionTaskUsed = "translate", EngineUsed = "old engine", ModelUsed = "old model",
        });
        fixture.Plugin.EnqueueText("recovered question mark");
        var recovered = await fixture.Orchestrator.RetryFromHistoryAsync(failed.Id);
        Assert.Equal(failed.Id, recovered.Id);
        Assert.True(fixture.Plugin.ReceivedTranslations[^1]);
        Assert.Equal("translate", recovered.TranscriptionTaskUsed);
        Assert.Equal(fixture.Plugin.ProviderId, recovered.EngineUsed);
        Assert.Equal(fixture.Plugin.SelectedModelId, recovered.ModelUsed);
        Assert.Equal(failed.Timestamp, recovered.Timestamp);
        Assert.Equal(failed.AudioFileName, recovered.AudioFileName);
        Assert.Equal(TranscriptionRecordStatus.Succeeded, recovered.Status);
        Assert.Null(recovered.FailureMessage);
        Assert.Equal("recovered?", recovered.FinalText);
        Assert.Equal(TextInsertionStatus.CopiedToClipboard, recovered.InsertionStatus);
        Assert.Equal(["recovered?"], fixture.InsertionPlatform.ClipboardWrites);
        Assert.Empty(fixture.InsertionPlatform.Typed);
        Assert.Equal(recovered, Assert.Single(fixture.History.Records));
    });

    [Fact]
    public Task ProcessingFailure_PreservesRawTextAndProfile_AndRetryUsesSamePipeline() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Provider.GetRequiredService<IPromptActionService>().AddAction(new PromptAction
        {
            Id = "recovery-prompt", Name = "Recovery prompt", SystemPrompt = "Rewrite the input.",
            ProviderOverride = "plugin:integration.scripted-llm:scripted-llm-model",
        });
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "recovery-profile", Name = "Recovery profile", PromptActionId = "recovery-prompt",
        });
        fixture.Llm.Failure = new InvalidOperationException("processing failed: preserved raw text");
        fixture.Plugin.EnqueueText("preserved raw text");
        await DictateAsync(fixture, "recovery-profile");
        var failed = Assert.Single(fixture.History.Records);
        Assert.Equal(TranscriptionRecordStatus.ProcessingFailed, failed.Status);
        Assert.Equal("preserved raw text", failed.RawText);
        Assert.Equal(failed.RawText, failed.FinalText);
        Assert.Equal("Recovery profile", failed.ProfileName);
        Assert.Equal("processing failed: [redacted]", failed.FailureMessage);
        fixture.Llm.Failure = null;
        fixture.Plugin.EnqueueText("new raw text");
        fixture.Llm.EnqueueStream("rewritten recovered text");
        var recovered = await fixture.Orchestrator.RetryFromHistoryAsync(failed.Id);
        Assert.Equal("rewritten recovered text", recovered.FinalText);
        Assert.True(recovered.PromptActionApplied);
    });

    [Fact]
    public Task RetryFailure_ReplacesFailure_AndMissingAudioIsLocalized() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("first failure");
        await DictateAsync(fixture);
        var failed = Assert.Single(fixture.History.Records);
        fixture.Plugin.EnqueueFailure("second failure");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.RetryFromHistoryAsync(failed.Id));
        Assert.Equal("second failure", Assert.Single(fixture.History.Records).FailureMessage);
        File.Delete(Path.Join(TypeWhisperEnvironment.AudioPath, failed.AudioFileName));
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Orchestrator.RetryFromHistoryAsync(failed.Id));
        Assert.Equal(Loc.Instance["History.RetryAudioMissing"], missing.Message);
        Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
    });

    [Theory]
    // A record that still reads as Succeeded keeps its own failure message: HasFailure gates the
    // display, so a message written there would never be shown.
    [InlineData(TranscriptionRecordStatus.Succeeded, "full transcript", "processed transcript", TextInsertionStatus.Failed, false)]
    [InlineData(TranscriptionRecordStatus.ProcessingFailed, "preserved raw text", "", TextInsertionStatus.Unknown, true)]
    public Task RetryProviderFailure_PreservesExistingTextAndStatus(
        TranscriptionRecordStatus status, string rawText, string finalText, TextInsertionStatus insertionStatus,
        bool updatesFailureMessage)
        => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("initial failure");
        await DictateAsync(fixture);
        var original = Assert.Single(fixture.History.Records) with
        {
            Status = status, RawText = rawText, FinalText = finalText,
            InsertionStatus = insertionStatus, InsertionFailureReason = "previous insertion failure",
            EngineUsed = "original engine", ModelUsed = "original model",
        };
        Assert.True(fixture.History.TryReplaceRecord(original));
        fixture.Plugin.EnqueueFailure("provider unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.RetryFromHistoryAsync(original.Id));

        var expected = updatesFailureMessage ? original with { FailureMessage = "provider unavailable" } : original;
        Assert.Equal(expected, Assert.Single(fixture.History.Records));
        Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
    });

    [Fact]
    public Task RetryAndRecording_ExcludeEachOtherInBothDirections() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("failure");
        await DictateAsync(fixture);
        var failed = Assert.Single(fixture.History.Records);
        var session = await fixture.Orchestrator.StartAsync();
        Assert.True(session > 0);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.RetryFromHistoryAsync(failed.Id));
        Assert.Equal(Loc.Instance["History.RetryBusy"], busy.Message);
        await fixture.Orchestrator.CancelAsync();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Plugin.EnqueueResult(async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return new PluginTranscriptionResult("recovered", "en", 1);
        });
        var retry = fixture.Orchestrator.RetryFromHistoryAsync(failed.Id);
        try
        {
            await BoundedTest.WaitAsync(entered.Task);
            var overlayStates = new ConcurrentQueue<DictationOverlayState>();
            void CaptureOverlay(object? _, DictationOverlayState state) => overlayStates.Enqueue(state);
            fixture.Orchestrator.OverlayStateChanged += CaptureOverlay;
            Assert.Equal(0, await fixture.Orchestrator.StartAsync());
            fixture.Orchestrator.OverlayStateChanged -= CaptureOverlay;
            Assert.False(fixture.Orchestrator.IsRecording);
            // The swallowed hotkey press must still tell the user why nothing happened.
            Assert.Contains(overlayStates, state =>
                state is { ShowFeedback: true, FeedbackIsError: true }
                && state.FeedbackText == Loc.Instance["History.RetryBusy"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Orchestrator.RetryFromHistoryAsync(failed.Id));
        }
        finally
        {
            release.TrySetResult();
        }
        await retry;
        Assert.True(await fixture.Orchestrator.StartAsync() > 0);
        await fixture.Orchestrator.CancelAsync();
    });

    [Theory]
    [InlineData("cancel")]
    [InlineData("caller")]
    [InlineData("shutdown")]
    public Task RetryCancellation_DrainsLeaseAndClearsRecovery(string cancellation) => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("initial failure");
        await DictateAsync(fixture);
        var failed = Assert.Single(fixture.History.Records);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Plugin.EnqueueResult(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new PluginTranscriptionResult("unreachable", "en", 1);
        });
        using var caller = new CancellationTokenSource();
        var retry = fixture.Orchestrator.RetryFromHistoryAsync(failed.Id, caller.Token);
        await BoundedTest.WaitAsync(entered.Task);
        switch (cancellation)
        {
            case "caller":
                await caller.CancelAsync();
                break;
            case "shutdown":
                await fixture.Orchestrator.CloseToggleGateAsync();
                await fixture.Orchestrator.CancelRecoveryAndDrainAsync();
                break;
            default:
                await fixture.Orchestrator.CancelAsync();
                break;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        await fixture.Provider.GetRequiredService<ModelManagerService>().UnloadModelAsync();
        Assert.Equal(failed, Assert.Single(fixture.History.Records));
        if (cancellation != "shutdown")
        {
            Assert.True(await fixture.Orchestrator.StartAsync() > 0);
            await fixture.Orchestrator.CancelAsync();
        }
    });

    [Theory]
    [InlineData(PluginRequestFailureKind.ServerError, true, 3, 1)]
    [InlineData(PluginRequestFailureKind.Authentication, false, 1, 0)]
    [InlineData(PluginRequestFailureKind.ServerError, false, 1, 0)]
    public Task PromptFailure_BoundsTotalCallsAndSanitizesProcessedInput(
        PluginRequestFailureKind kind, bool transient, int streamCalls, int batchCalls) => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Provider.GetRequiredService<IPromptActionService>().AddAction(new PromptAction
        {
            Id = "prompt", Name = "Prompt", SystemPrompt = "Rewrite the input.",
            ProviderOverride = "plugin:integration.scripted-llm:scripted-llm-model",
        });
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "profile", Name = "Profile", PromptActionId = "prompt",
        });
        fixture.Llm.Failure = new PluginRequestException("processing failed: hello?", kind,
            retryAfter: TimeSpan.Zero, isTransient: transient);
        var overlays = new ConcurrentQueue<DictationOverlayState>();
        fixture.Orchestrator.OverlayStateChanged += (_, state) => overlays.Enqueue(state);
        fixture.Plugin.EnqueueText("hello question mark");
        await DictateAsync(fixture, "profile");
        Assert.Equal(streamCalls, fixture.Llm.StreamCalls);
        Assert.Equal(batchCalls, fixture.Llm.BatchCalls);
        var failed = Assert.Single(fixture.History.Records);
        Assert.Equal("hello question mark", failed.RawText);
        Assert.Equal("processing failed: [redacted]", failed.FailureMessage);
        Assert.DoesNotContain("hello?", fixture.Provider.GetRequiredService<IErrorLogService>().ExportDiagnostics());
        Assert.All(overlays, state => Assert.DoesNotContain("hello?", state.FeedbackText ?? ""));
    });

    private static async Task DictateAsync(OrchestratorCompositionFixture fixture, string? profileId = null)
    {
        Assert.True(await fixture.Orchestrator.StartAsync(profileId) > 0);
        fixture.FeedNonSilentAudio();
        await fixture.Orchestrator.StopAsync();
    }
}
