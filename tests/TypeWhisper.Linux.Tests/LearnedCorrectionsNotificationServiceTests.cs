using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Orchestration tests for <see cref="LearnedCorrectionsNotificationService" /> on tiling
///     WMs. The D-Bus transport is behind <c>INotificationChannel</c> (not unit-testable), so
///     a fake channel captures the calls and simulates the daemon's ActionInvoked /
///     NotificationClosed signals. Presenter access is marshalled through an injected post,
///     driven synchronously here so no dispatcher is needed.
/// </summary>
public sealed class LearnedCorrectionsNotificationServiceTests : IDisposable
{
    // Enables DesktopDetector.UsesNotificationRecordingIndicator() so the service isn't inert.
    private const string HyprlandSignatureEnv = "HYPRLAND_INSTANCE_SIGNATURE";

    // The detector also consults these; neutralize every signal so the host session (a Hyprland/
    // Sway/River/Niri dev box or CI) can't flip a supposedly-disabled test back on, or vice versa.
    private static readonly string[] s_detectorEnvVars =
        [HyprlandSignatureEnv, "SWAYSOCK", "XDG_CURRENT_DESKTOP"];

    private readonly Dictionary<string, string?> _originalDetectorEnv =
        s_detectorEnvVars.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable
        );

    private readonly string _dictionaryPath;
    private readonly string _tempDir;

    // Every service CreateSut hands out, so teardown exercises its disposal path (unsubscribe +
    // channel close) instead of leaving it wired to the test's learning service.
    private readonly List<LearnedCorrectionsNotificationService> _services = [];

    public LearnedCorrectionsNotificationServiceTests()
    {
        // Clear the other detector signals, then set only the Hyprland one, so every test's
        // enabled/disabled state is decided solely by HYPRLAND_INSTANCE_SIGNATURE below.
        foreach (var name in s_detectorEnvVars)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable(HyprlandSignatureEnv, "test-session");
        _tempDir = TestPaths.CreateTempDirectory("TypeWhisper.LearnedNotify.Tests");
        _dictionaryPath = Path.Join(_tempDir, "dictionary.json");
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        foreach (var (name, value) in _originalDetectorEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void LearnedEvent_ShowsNotificationWithUndo()
    {
        var (learning, _, channel, _, service) = CreateSut();
        service.Initialize();

        RaiseLearned(learning, [Correction("kubernets", "Kubernetes")]);

        var call = Assert.Single(channel.ShowCalls);
        Assert.Equal(0u, call.ReplacesId); // first popup: no replace
        Assert.True(call.WithUndoAction);
        Assert.Contains("kubernets", call.Summary);
        Assert.Contains("Kubernetes", call.Summary);
    }

    [Fact]
    public void UndoConfirmation_ReplacesSamePopupInPlace()
    {
        var (learning, dictionary, channel, _, service) = CreateSut();
        // Seed the entry so Undo removes a real correction.
        var learned = Seed(dictionary, "kubernets", "Kubernetes");
        service.Initialize();

        RaiseLearned(learning, learned);
        var shownId = channel.ShowCalls[0].ResultId;

        channel.RaiseActionInvoked(shownId, "undo");

        // Undo emits the 2s confirmation, which must replace the SAME popup, not stack a new one.
        Assert.Equal(2, channel.ShowCalls.Count);
        var confirmation = channel.ShowCalls[1];
        Assert.Equal(shownId, confirmation.ReplacesId);
        Assert.False(confirmation.WithUndoAction);
        Assert.Empty(channel.CloseCalls);
        // The correction was actually undone in the dictionary.
        Assert.Empty(dictionary.GetCorrections());
    }

    [Fact]
    public void ActionInvoked_Undo_WhenDictionaryThrows_DoesNotEscapeCallbackAndLogsFailure()
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary
            .Setup(d => d.UndoLearnedCorrections(
                It.IsAny<IEnumerable<LearnedDictionaryCorrection>>()))
            .Throws(new IOException("disk full"));
        var learning = new TargetAppCorrectionLearningService(
            Mock.Of<IAtSpiEventClient>(),
            dictionary.Object,
            Mock.Of<ISettingsService>(),
            Mock.Of<IErrorLogService>());
        var channel = new FakeChannel();
        var scheduler = new FakeScheduler();
        var errorLog = new Mock<IErrorLogService>();
        var service = new LearnedCorrectionsNotificationService(
            learning,
            dictionary.Object,
            errorLog.Object,
            channel,
            post: action => action(),
            scheduleDelay: scheduler.Schedule);
        service.Initialize();

        RaiseLearned(learning, [Correction("teh", "the")]);
        var shownId = channel.ShowCalls[0].ResultId;

        var thrown = Record.Exception(() => channel.RaiseActionInvoked(shownId, "undo"));

        Assert.Null(thrown);
        errorLog.Verify(
            e => e.AddEntry(It.IsAny<string>(), ErrorCategory.General),
            Times.Once);
        Assert.Equal(2, channel.ShowCalls.Count);
        Assert.True(channel.ShowCalls[1].WithUndoAction);
    }

    [Fact]
    public void HiddenFeedback_ClosesTheNotification()
    {
        var (learning, _, channel, scheduler, service) = CreateSut();
        service.Initialize();

        RaiseLearned(learning, [Correction("teh", "the")]);
        var shownId = channel.ShowCalls[0].ResultId;

        // The presenter's 8s auto-hide fires an empty-text feedback → close by id.
        scheduler.FirePending();

        var closedId = Assert.Single(channel.CloseCalls);
        Assert.Equal(shownId, closedId);
    }

    [Fact]
    public void ActionInvoked_OnForeignId_IsIgnored()
    {
        var (learning, dictionary, channel, _, service) = CreateSut();
        var learned = Seed(dictionary, "kubernets", "Kubernetes");
        service.Initialize();

        RaiseLearned(learning, learned);

        // A signal for a notification that isn't ours must not touch the dictionary.
        channel.RaiseActionInvoked(channel.ShowCalls[0].ResultId + 999u, "undo");

        Assert.Single(channel.ShowCalls);
        Assert.Single(dictionary.GetCorrections());
    }

    [Fact]
    public void NotificationClosed_ClearsBatch_SoLaterUndoNoOps()
    {
        var (learning, dictionary, channel, _, service) = CreateSut();
        var learned = Seed(dictionary, "kubernets", "Kubernetes");
        service.Initialize();

        RaiseLearned(learning, learned);
        var shownId = channel.ShowCalls[0].ResultId;

        // User dismissed the popup → the batch is dropped.
        channel.RaiseNotificationClosed(shownId, reason: 2);

        // A late ActionInvoked for the now-forgotten id must be a no-op (nothing pending, and
        // the id no longer matches ours).
        channel.RaiseActionInvoked(shownId, "undo");

        Assert.Single(channel.ShowCalls); // no confirmation popup
        Assert.Single(dictionary.GetCorrections()); // correction stays learned
    }

    [Fact]
    public void Disabled_OnDesktopEnvironment_IsFullyInert()
    {
        // Clear the tiling-WM signal so UsesNotificationRecordingIndicator() is false.
        Environment.SetEnvironmentVariable(HyprlandSignatureEnv, null);
        var (learning, _, channel, _, service) = CreateSut();
        service.Initialize();

        RaiseLearned(learning, [Correction("teh", "the")]);

        // No subscription happened, so the learned event produces no notification traffic.
        Assert.Empty(channel.ShowCalls);
        Assert.Empty(channel.CloseCalls);
    }

    private (TargetAppCorrectionLearningService Learning, DictionaryService Dictionary,
        FakeChannel Channel, FakeScheduler Scheduler, LearnedCorrectionsNotificationService Service)
        CreateSut()
    {
        var dictionary = new DictionaryService(_dictionaryPath);
        var learning = new TargetAppCorrectionLearningService(
            Mock.Of<IAtSpiEventClient>(),
            dictionary,
            Mock.Of<ISettingsService>(),
            Mock.Of<IErrorLogService>()
        );
        var channel = new FakeChannel();
        var scheduler = new FakeScheduler();
        var service = new LearnedCorrectionsNotificationService(
            learning,
            dictionary,
            Mock.Of<IErrorLogService>(),
            channel,
            // Synchronous post: the presenter is single-threaded here, mirroring the real
            // Dispatcher.UIThread serialization without a headless dispatcher.
            post: action => action(),
            scheduleDelay: scheduler.Schedule
        );
        _services.Add(service);
        return (learning, dictionary, channel, scheduler, service);
    }

    private static LearnedDictionaryCorrection Correction(string original, string replacement)
    {
        return new LearnedDictionaryCorrection(Guid.NewGuid().ToString("N"), original, replacement);
    }

    // Persists a correction and returns the batch the way the learning service would emit it,
    // so Undo has a real dictionary entry to remove.
    private static IReadOnlyList<LearnedDictionaryCorrection> Seed(
        DictionaryService dictionary,
        string original,
        string replacement
    )
    {
        var learned = dictionary.LearnCorrections([new CorrectionSuggestion(original, replacement)]);
        Assert.NotEmpty(learned);
        return learned;
    }

    private static void RaiseLearned(
        TargetAppCorrectionLearningService learning,
        IReadOnlyList<LearnedDictionaryCorrection> learned
    )
    {
        learning.RaiseCorrectionsLearnedForTest(learned);
    }

    // Fake INotificationChannel: records Show/Close and lets the test raise the daemon's
    // signals. ShowAsync hands back a stable, unique id per call so replaces_id can be checked.
    private sealed class FakeChannel : LearnedCorrectionsNotificationService.INotificationChannel
    {
        private uint _nextId = 100;

        public List<ShowCall> ShowCalls { get; } = [];
        public List<uint> CloseCalls { get; } = [];

        public event Action<uint, string>? ActionInvoked;
        public event Action<uint, uint>? Closed;

        public Task<uint> ShowAsync(uint replacesId, string summary, bool withUndoAction)
        {
            var id = replacesId == 0 ? _nextId++ : replacesId;
            ShowCalls.Add(new ShowCall(replacesId, summary, withUndoAction, id));
            return Task.FromResult(id);
        }

        public Task CloseAsync(uint id)
        {
            CloseCalls.Add(id);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public void RaiseActionInvoked(uint id, string action)
        {
            ActionInvoked?.Invoke(id, action);
        }

        public void RaiseNotificationClosed(uint id, uint reason)
        {
            Closed?.Invoke(id, reason);
        }

        public sealed record ShowCall(uint ReplacesId, string Summary, bool WithUndoAction, uint ResultId);
    }
}
