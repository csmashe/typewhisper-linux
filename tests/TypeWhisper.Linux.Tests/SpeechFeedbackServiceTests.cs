using System.Collections.Concurrent;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpeechFeedbackServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public void AvailableProviders_includes_system_and_plugin_tts()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        var providers = sut.AvailableProviders.Select(provider => provider.Id).ToArray();

        Assert.Equal(["linux-system", "cloud"], providers);
    }

    [Fact]
    public void SelectVoice_passes_default_voice_as_null()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var manager = TestPluginManagerFactory.Create();
        var systemProvider = new FakeTtsProvider("linux-system", "Linux system", true);
        using var sut = new SpeechFeedbackService(settings.Object, manager, systemProvider);

        sut.SelectVoice("linux-system", SpeechFeedbackService.DefaultVoiceOptionId);

        Assert.Null(systemProvider.SelectedVoiceId);
    }

    [Fact]
    public void EffectiveProvider_falls_back_to_system_when_selected_plugin_is_not_configured()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackProviderId = "cloud" }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", false);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        Assert.Equal("linux-system", sut.EffectiveProviderId);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_substitutes_configured_language_when_request_has_none()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud",
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription("Hallo Welt");

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

        var request = Assert.Single(plugin.Requests);
        Assert.Equal("de", request.Language);
        Assert.Equal(TtsPurpose.Transcription, request.Purpose);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_keeps_explicit_request_language()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud",
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription("Hallo Welt", "fr");

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

        var request = Assert.Single(plugin.Requests);
        Assert.Equal("fr", request.Language);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_skips_configured_language_fallback_when_disabled()
    {
        // Callers that have already resolved the readback language opt out of
        // the configured-language fallback; a null language must stay null.
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud",
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription(
            "Hallo Welt",
            language: null,
            useConfiguredLanguageFallback: false
        );

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

        var request = Assert.Single(plugin.Requests);
        Assert.Null(request.Language);
        Assert.Equal(TtsPurpose.Transcription, request.Purpose);
    }

    [Fact]
    public async Task Stop_before_capture_stops_prior_playback_before_awaiting_completion()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var priorSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(priorSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("prior playback");
        await priorSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        using var reservation = sut.ReserveStartupFeedback();
        var stop = reservation.StopPriorPlaybackAsync();

        Assert.Equal(1, priorSession.StopCount);
        Assert.False(stop.IsCompleted);

        priorSession.Complete();
        await stop.WaitAsync(s_testGuard);
    }

    [Fact]
    public async Task Stop_before_capture_returns_at_its_finite_bound_when_completion_is_missing()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var priorSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(priorSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("prior playback");
        await priorSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        using var reservation = sut.ReserveStartupFeedback();
        var stop = reservation.StopPriorPlaybackAsync();
        var timeout = await delay.NextRequestAsync();

        Assert.Equal(SpeechFeedbackService.s_stopPlaybackTimeout, timeout.Duration);
        Assert.Equal(1, priorSession.StopCount);
        timeout.Complete();
        await stop.WaitAsync(s_testGuard);
    }

    [Fact]
    public async Task Recording_start_announcement_awaits_session_completion()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var session = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(session);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        using var reservation = sut.ReserveStartupFeedback();
        var announcement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        await session.HandlerAttached.Task.WaitAsync(s_testGuard);

        Assert.False(announcement.IsCompleted);
        session.Complete();
        await announcement.WaitAsync(s_testGuard);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Recording_start_announcement_timeout_stops_session_before_returning()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var session = new ControlledPlaybackSession(completeOnStop: true);
        var provider = new ControlledTtsProvider(session);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        using var reservation = sut.ReserveStartupFeedback();
        var announcement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        await session.HandlerAttached.Task.WaitAsync(s_testGuard);
        var timeout = await delay.NextRequestAsync();

        Assert.Equal(SpeechFeedbackService.s_recordingAnnouncementTimeout, timeout.Duration);
        Assert.Equal(0, session.StopCount);
        timeout.Complete();

        await announcement.WaitAsync(s_testGuard);
        Assert.Equal(1, session.StopCount);
    }

    [Fact]
    public async Task Older_completion_cannot_clear_or_complete_newer_request()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var olderSession = new ControlledPlaybackSession();
        var newerSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(olderSession, newerSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("older playback");
        await olderSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        using var reservation = sut.ReserveStartupFeedback();
        var stopOlder = reservation.StopPriorPlaybackAsync();
        var newerAnnouncement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        await newerSession.HandlerAttached.Task.WaitAsync(s_testGuard);
        Assert.Equal(1, olderSession.StopCount);

        olderSession.Complete();
        await stopOlder.WaitAsync(s_testGuard);
        using var replacementReservation = sut.ReserveStartupFeedback();
        var stopNewer = replacementReservation.StopPriorPlaybackAsync();

        Assert.Equal(1, newerSession.StopCount);
        Assert.False(newerAnnouncement.IsCompleted);
        newerSession.Complete();
        await Task.WhenAll(newerAnnouncement, stopNewer).WaitAsync(s_testGuard);
    }

    [Fact]
    public async Task Concurrent_starts_serialize_version_allocation_and_publication()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var allocations = new ConcurrentQueue<(long Version, bool LockHeld)>();
        var firstAllocationReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstAllocation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            playbackVersionAllocated: (version, lockHeld) =>
            {
                allocations.Enqueue((version, lockHeld));
                if (version != 1)
                {
                    return;
                }

                firstAllocationReached.TrySetResult();
                releaseFirstAllocation.Task.WaitAsync(s_testGuard).GetAwaiter().GetResult();
            }
        );

        var olderStart = Task.Run(() =>
            // ReSharper disable once AccessToDisposedClosure -- Task.WhenAll below awaits completion before sut disposal
            sut.SpeakAutomaticTranscription("older playback")
        );
        await firstAllocationReached.Task.WaitAsync(s_testGuard);

        var newerStartAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var newerStart = Task.Run(() =>
        {
            newerStartAttempted.TrySetResult();
            // ReSharper disable once AccessToDisposedClosure -- Task.WhenAll below awaits completion before sut disposal
            sut.SpeakAutomaticTranscription("newer playback");
        });
        await newerStartAttempted.Task.WaitAsync(s_testGuard);

        releaseFirstAllocation.TrySetResult();
        await Task.WhenAll(olderStart, newerStart).WaitAsync(s_testGuard);

        Assert.Equal([(1, true), (2, true)], allocations.ToArray());

        var firstCall = await provider.NextRequestAsync();
        var secondCall = await provider.NextRequestAsync();
        var calls = new[] { firstCall, secondCall }.ToDictionary(
            call => call.Request.Text
        );
        var olderCall = calls["older playback"];
        var newerCall = calls["newer playback"];
        Assert.True(olderCall.CancellationToken.IsCancellationRequested);
        Assert.False(newerCall.CancellationToken.IsCancellationRequested);

        var newerSession = new ControlledPlaybackSession();
        newerCall.Return(newerSession);
        await newerSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        var olderSession = new ControlledPlaybackSession();
        olderCall.Return(olderSession);
        await olderSession.StopCalled.Task.WaitAsync(s_testGuard);

        Assert.Equal(0, olderSession.SubscriberCount);
        Assert.True(newerSession.IsActive);
        Assert.Equal(0, newerSession.StopCount);
        sut.ReadBack("toggle newer playback");
        Assert.Equal(1, newerSession.StopCount);
        Assert.Equal(2, provider.Requests.Length);

        newerSession.Complete();

        sut.ReadBack("subsequent readback");
        var readBackCall = await provider.NextRequestAsync();
        Assert.Equal("subsequent readback", readBackCall.Request.Text);
        Assert.Equal(TtsPurpose.ManualReadback, readBackCall.Request.Purpose);

        var readBackSession = new ControlledPlaybackSession();
        readBackCall.Return(readBackSession);
        await readBackSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        olderSession.Complete();
        sut.ReadBack("toggle current readback");

        Assert.Equal(1, readBackSession.StopCount);
        Assert.Equal(3, provider.Requests.Length);
        readBackSession.Complete();
    }

    [Fact]
    public async Task Recording_timeout_releases_late_cancellation_ignoring_request()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        var reservation = sut.ReserveStartupFeedback();
        var announcement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        var announcementCall = await provider.NextRequestAsync();
        var announcementTimeout = await delay.NextRequestAsync();

        Assert.Equal(
            SpeechFeedbackService.s_recordingAnnouncementTimeout,
            announcementTimeout.Duration
        );
        announcementTimeout.Complete();

        var cleanupTimeout = await delay.NextRequestAsync();
        Assert.Equal(
            SpeechFeedbackService.s_stopPlaybackTimeout,
            cleanupTimeout.Duration
        );
        Assert.True(announcementCall.CancellationToken.IsCancellationRequested);

        var lateSession = new ControlledPlaybackSession();
        announcementCall.Return(lateSession);
        await lateSession.StopCalled.Task.WaitAsync(s_testGuard);
        Assert.Equal(1, lateSession.StopCount);
        Assert.Equal(0, lateSession.SubscriberCount);

        cleanupTimeout.Complete();
        await announcement.WaitAsync(s_testGuard);
        reservation.Dispose();

        sut.ReadBack("manual readback");
        var readBackCall = await provider.NextRequestAsync();
        Assert.Equal("manual readback", readBackCall.Request.Text);
        Assert.Equal(TtsPurpose.ManualReadback, readBackCall.Request.Purpose);

        var readBackSession = new ControlledPlaybackSession();
        readBackCall.Return(readBackSession);
        await readBackSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        lateSession.Complete();
        readBackSession.Complete();
    }

    [Fact]
    public async Task Recording_timeout_releases_hung_provider_request()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        var reservation = sut.ReserveStartupFeedback();
        var announcement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        var announcementCall = await provider.NextRequestAsync();
        var announcementTimeout = await delay.NextRequestAsync();

        Assert.Equal(
            SpeechFeedbackService.s_recordingAnnouncementTimeout,
            announcementTimeout.Duration
        );
        announcementTimeout.Complete();

        var cleanupTimeout = await delay.NextRequestAsync();
        Assert.Equal(
            SpeechFeedbackService.s_stopPlaybackTimeout,
            cleanupTimeout.Duration
        );
        Assert.True(announcementCall.CancellationToken.IsCancellationRequested);
        cleanupTimeout.Complete();
        await announcement.WaitAsync(s_testGuard);
        reservation.Dispose();

        Assert.Single(provider.Requests);
        sut.ReadBack("manual readback after hung announcement");
        Assert.Equal(2, provider.Requests.Length);

        var readBackCall = await provider.NextRequestAsync();
        Assert.Equal("manual readback after hung announcement", readBackCall.Request.Text);
        Assert.Equal(TtsPurpose.ManualReadback, readBackCall.Request.Purpose);

        var readBackSession = new ControlledPlaybackSession();
        readBackCall.Return(readBackSession);
        await readBackSession.HandlerAttached.Task.WaitAsync(s_testGuard);
        readBackSession.Complete();
    }

    [Fact]
    public async Task Reservation_cancels_pending_provider_and_waits_only_to_the_stop_bound()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("pending readback");
        var pendingCall = await provider.NextRequestAsync();

        using var reservation = sut.ReserveStartupFeedback();
        var stop = reservation.StopPriorPlaybackAsync();
        var timeout = await delay.NextRequestAsync();

        Assert.True(pendingCall.CancellationToken.IsCancellationRequested);
        Assert.Equal(SpeechFeedbackService.s_stopPlaybackTimeout, timeout.Duration);
        timeout.Complete();
        await stop.WaitAsync(s_testGuard);

        var lateSession = new ControlledPlaybackSession();
        pendingCall.Return(lateSession);
        await lateSession.StopCalled.Task.WaitAsync(s_testGuard);
        Assert.Equal(1, lateSession.StopCount);
        Assert.Equal(0, lateSession.SubscriberCount);
        lateSession.Complete();
    }

    [Fact]
    public async Task Reservation_blocks_publication_reentered_from_prior_session_stop()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var allocations = new ConcurrentQueue<(long Version, bool LockHeld)>();
        SpeechFeedbackService? service = null;
        var priorSession = new ControlledPlaybackSession(
            // ReSharper disable once AccessToModifiedClosure -- deliberate late binding: the session must call back into the service that is constructed below it.
            onStop: () => service!.AnnounceError("reentered during stop")
        );
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            playbackVersionAllocated: (version, lockHeld) =>
                allocations.Enqueue((version, lockHeld))
        );
        service = sut;
        sut.SpeakAutomaticTranscription("prior playback");
        var priorCall = await provider.NextRequestAsync();
        priorCall.Return(priorSession);
        await priorSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        using var reservation = sut.ReserveStartupFeedback();

        Assert.Equal(1, priorSession.StopCount);
        Assert.Single(provider.Requests);
        Assert.Equal([(1, true)], allocations.ToArray());

        priorSession.Complete();
        await reservation.StopPriorPlaybackAsync().WaitAsync(s_testGuard);
    }

    [Fact]
    public void Ordinary_readback_and_error_are_rejected_without_allocating_while_reserved()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var allocations = new ConcurrentQueue<(long Version, bool LockHeld)>();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            playbackVersionAllocated: (version, lockHeld) =>
                allocations.Enqueue((version, lockHeld))
        );
        using var reservation = sut.ReserveStartupFeedback();

        sut.SpeakAutomaticTranscription("late readback");
        sut.AnnounceError("late error");
        sut.ReadBack("late manual readback");

        Assert.Empty(provider.Requests);
        Assert.Empty(allocations);
    }

    [Fact]
    public async Task Only_matching_current_lease_can_bypass_reservation_for_start_cue()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var session = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(session);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        var reservation = sut.ReserveStartupFeedback();

        var matchingAnnouncement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );
        await session.HandlerAttached.Task.WaitAsync(s_testGuard);

        sut.AnnounceError("must not supersede start cue");
        sut.ReadBack("must not stop start cue");
        var foreignAnnouncement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: new ForeignStartupFeedbackReservation()
        );

        Assert.True(foreignAnnouncement.IsCompletedSuccessfully);
        Assert.Single(provider.Requests);
        Assert.Equal(0, session.StopCount);

        session.Complete();
        await matchingAnnouncement.WaitAsync(s_testGuard);
        reservation.Dispose();

        var staleAnnouncement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true,
            reservation: reservation
        );

        Assert.True(staleAnnouncement.IsCompletedSuccessfully);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Release_does_not_replay_suppressed_speech_and_later_request_plays()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new FakeTtsProvider("linux-system", "Linux system", true);
        using var sut = new SpeechFeedbackService(settings.Object, manager, provider);
        var reservation = sut.ReserveStartupFeedback();

        sut.AnnounceError("suppressed");
        reservation.Dispose();

        Assert.Empty(provider.Requests);

        sut.AnnounceError("explicit later request");
        await provider.RequestReceived.Task.WaitAsync(s_testGuard);

        var request = Assert.Single(provider.Requests);
        Assert.Contains("explicit later request", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_dispose_cannot_release_newer_reservation_and_dispose_is_idempotent()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new FakeTtsProvider("linux-system", "Linux system", true);
        using var sut = new SpeechFeedbackService(settings.Object, manager, provider);
        var staleReservation = sut.ReserveStartupFeedback();
        var currentReservation = sut.ReserveStartupFeedback();

        staleReservation.Dispose();
        staleReservation.Dispose();
        sut.AnnounceError("still suppressed");

        Assert.Empty(provider.Requests);

        currentReservation.Dispose();
        currentReservation.Dispose();
        sut.AnnounceError("released");
        await provider.RequestReceived.Task.WaitAsync(s_testGuard);

        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Concurrent_allocation_and_reservation_are_linearized_by_same_lock()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var provider = new ControlledTtsProvider(controlResponses: true);
        var allocations = new ConcurrentQueue<(long Version, bool LockHeld)>();
        var allocationReached = NewSignal();
        var releaseAllocation = NewSignal();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            playbackVersionAllocated: (version, lockHeld) =>
            {
                allocations.Enqueue((version, lockHeld));
                allocationReached.TrySetResult();
                releaseAllocation.Task.WaitAsync(s_testGuard).GetAwaiter().GetResult();
            }
        );

        var ordinaryStart = Task.Run(() =>
            // ReSharper disable once AccessToDisposedClosure -- both tasks finish before sut disposal
            sut.SpeakAutomaticTranscription("allocation winner")
        );
        await allocationReached.Task.WaitAsync(s_testGuard);

        var reserveAttempted = NewSignal();
        var reserveTask = Task.Run(() =>
        {
            reserveAttempted.TrySetResult();
            // ReSharper disable once AccessToDisposedClosure -- both tasks finish before sut disposal
            return sut.ReserveStartupFeedback();
        });
        await reserveAttempted.Task.WaitAsync(s_testGuard);

        releaseAllocation.TrySetResult();
        var reservation = await reserveTask.WaitAsync(s_testGuard);
        await ordinaryStart.WaitAsync(s_testGuard);

        Assert.Equal([(1, true)], allocations.ToArray());
        var providerCall = await provider.NextRequestAsync();
        Assert.True(providerCall.CancellationToken.IsCancellationRequested);

        sut.AnnounceError("reserved loser");
        Assert.Equal([(1, true)], allocations.ToArray());
        Assert.Single(provider.Requests);

        var lateSession = new ControlledPlaybackSession();
        providerCall.Return(lateSession);
        await lateSession.StopCalled.Task.WaitAsync(s_testGuard);
        reservation.Dispose();
        lateSession.Complete();
    }

    [Fact]
    public void Terminal_feedback_is_suppressed_during_reservation_and_released_on_dispose()
    {
        // start.wav's bypass is structural — it never enters TryRunOrdinaryFeedback —
        // and is pinned by the orchestrator helper tests, not simulated here.
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var manager = TestPluginManagerFactory.Create();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );
        var reservation = sut.ReserveStartupFeedback();
        var terminalCount = 0;

        Assert.False(sut.TryRunOrdinaryFeedback(() => terminalCount++));
        Assert.Equal(0, terminalCount);

        reservation.Dispose();
        Assert.True(sut.TryRunOrdinaryFeedback(() => terminalCount++));
        Assert.Equal(1, terminalCount);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledDelay
    {
        private readonly Queue<DelayRequest> _requests = new();
        private readonly SemaphoreSlim _requestAvailable = new(0);

        public Task WaitAsync(TimeSpan duration)
        {
            var request = new DelayRequest(duration);
            lock (_requests)
            {
                _requests.Enqueue(request);
            }

            _requestAvailable.Release();
            return request.Completion.Task;
        }

        public async Task<DelayRequest> NextRequestAsync()
        {
            await _requestAvailable.WaitAsync().WaitAsync(s_testGuard);
            lock (_requests)
            {
                return _requests.Dequeue();
            }
        }
    }

    private sealed class DelayRequest(TimeSpan duration)
    {
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public void Complete()
        {
            Completion.TrySetResult();
        }
    }

    private sealed class ControlledTtsProvider : ITtsProviderPlugin
    {
        private readonly bool _controlResponses;
        private readonly Lock _sync = new();
        private readonly Queue<ControlledProviderCall> _calls = new();
        private readonly SemaphoreSlim _callAvailable = new(0);
        private readonly List<TtsSpeakRequest> _requests = [];
        private readonly Queue<ITtsPlaybackSession> _sessions;

        public ControlledTtsProvider(params ITtsPlaybackSession[] sessions)
        {
            _sessions = new Queue<ITtsPlaybackSession>(sessions);
        }

        public ControlledTtsProvider(bool controlResponses)
        {
            _controlResponses = controlResponses;
            _sessions = new Queue<ITtsPlaybackSession>();
        }

        public string PluginId => "plugin.controlled";
        public string PluginName => "Controlled";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "controlled";
        public string ProviderDisplayName => "Controlled";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices => [];
        public string? SelectedVoiceId => null;
        public TtsSpeakRequest[] Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectVoice(string? voiceId) { }

        public Task<ITtsPlaybackSession> SpeakAsync(
            TtsSpeakRequest request,
            CancellationToken ct
        )
        {
            ControlledProviderCall call;
            ITtsPlaybackSession? session = null;
            lock (_sync)
            {
                _requests.Add(request);
                call = new ControlledProviderCall(request, ct);
                _calls.Enqueue(call);
                if (!_controlResponses)
                {
                    Assert.True(
                        _sessions.TryDequeue(out session),
                        $"No playback session was queued for the SpeakAsync request '{request.Text}'."
                    );
                }
            }

            _callAvailable.Release();
            if (session is not null)
            {
                call.Return(session);
            }

            return call.Session.Task;
        }

        public async Task<ControlledProviderCall> NextRequestAsync()
        {
            await _callAvailable.WaitAsync().WaitAsync(s_testGuard);
            lock (_sync)
            {
                return _calls.Dequeue();
            }
        }

        public void Dispose() { }
    }

    private sealed class ControlledProviderCall(
        TtsSpeakRequest request,
        CancellationToken cancellationToken
    )
    {
        public TtsSpeakRequest Request { get; } = request;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<ITtsPlaybackSession> Session { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public void Return(ITtsPlaybackSession session)
        {
            Session.TrySetResult(session);
        }
    }

    private sealed class ControlledPlaybackSession(
        bool completeOnStop = false,
        Action? onStop = null
    )
        : ITtsPlaybackSession
    {
        private readonly Lock _sync = new();
        private EventHandler? _completed;
        private bool _isActive = true;
        private int _stopCount;

        public bool IsActive
        {
            get
            {
                lock (_sync)
                {
                    return _isActive;
                }
            }
        }

        public int StopCount => Volatile.Read(ref _stopCount);
        public int SubscriberCount
        {
            get
            {
                lock (_sync)
                {
                    return _completed?.GetInvocationList().Length ?? 0;
                }
            }
        }
        public TaskCompletionSource HandlerAttached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public TaskCompletionSource StopCalled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public event EventHandler? Completed
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                var alreadyCompleted = false;
                lock (_sync)
                {
                    if (_isActive)
                    {
                        _completed += value;
                    }
                    else
                    {
                        alreadyCompleted = true;
                    }
                }

                HandlerAttached.TrySetResult();
                if (alreadyCompleted)
                {
                    value(this, EventArgs.Empty);
                }
            }
            remove
            {
                lock (_sync)
                {
                    _completed -= value;
                }
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _stopCount);
            StopCalled.TrySetResult();
            onStop?.Invoke();
            if (completeOnStop)
            {
                Complete();
            }
        }

        public void Complete()
        {
            EventHandler? handlers;
            lock (_sync)
            {
                if (!_isActive)
                {
                    return;
                }

                _isActive = false;
                handlers = _completed;
                _completed = null;
            }

            handlers?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ForeignStartupFeedbackReservation : IStartupFeedbackReservation
    {
        public Task StopPriorPlaybackAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class FakeTtsProvider(string providerId, string displayName, bool configured)
        : ITtsProviderPlugin
    {
        public string PluginId => $"plugin.{providerId}";
        public string PluginName => displayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId => providerId;
        public string ProviderDisplayName => displayName;
        public bool IsConfigured => configured;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } = [new("voice", "Voice")];
        public string? SelectedVoiceId { get; private set; }
        public List<TtsSpeakRequest> Requests { get; } = [];
        public TaskCompletionSource RequestReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectVoice(string? voiceId)
        {
            SelectedVoiceId = voiceId;
        }

        public Task<ITtsPlaybackSession> SpeakAsync(
            TtsSpeakRequest request,
            CancellationToken ct
        )
        {
            Requests.Add(request);
            RequestReceived.TrySetResult();
            return Task.FromResult<ITtsPlaybackSession>(InactiveSession.Instance);
        }

        public void Dispose() { }
    }

    private sealed class InactiveSession : ITtsPlaybackSession
    {
        public static InactiveSession Instance { get; } = new();
        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add { value?.Invoke(this, EventArgs.Empty); }
            remove { }
        }

        public void Stop() { }
    }
}
