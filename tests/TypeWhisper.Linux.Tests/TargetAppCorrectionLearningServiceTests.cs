using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers <see cref="TargetAppCorrectionLearningService" />: the silent
///     arm → observe edit → commit → learn loop (driven by a fake
///     <see cref="IAtSpiEventClient" />) and the static <see cref="TargetAppCorrectionLearningService.ShouldArm" />
///     gating matrix.
/// </summary>
public sealed class TargetAppCorrectionLearningServiceTests : IDisposable
{
    private static readonly AtSpiElementRef s_field = new("app", "/field/1");
    private static readonly AtSpiElementRef s_otherField = new("app", "/field/2");

    private readonly string _dictionaryPath =
        Path.Join(Path.GetTempPath(), $"tw-dict-{Guid.NewGuid():N}.json");

    private readonly DictionaryService _dictionary;

    public TargetAppCorrectionLearningServiceTests()
    {
        _dictionary = new DictionaryService(_dictionaryPath);
    }

    public void Dispose()
    {
        if (File.Exists(_dictionaryPath))
        {
            File.Delete(_dictionaryPath);
        }
    }

    [Fact]
    public async Task Arm_ThenEdit_ThenFocusOut_LearnsCorrectionSilently()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // User types over the misrecognized word to fix it.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);

        // ...then moves focus away, which commits the edit.
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_FocusOutWithoutEdit_LearnsNothing()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "hello world";
        await service.ArmAsync("hello world");

        // Focus leaves without any text-changed event — nothing to learn.
        client.RaiseFocus(s_otherField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_EditRejectedByCorrectionService_LearnsNothing()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "this is a rough draft for tomorrow";
        await service.ArmAsync("this is a rough draft for tomorrow");

        // A wholesale rewrite is rejected by CorrectionSuggestionService's safety gates.
        client.TextToReturn = "please send a concise status update instead";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_WhenDisabled_DoesNotTouchAtSpiOrLearn()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: false);

        client.TextToReturn = "hello world";
        await service.ArmAsync("hello world");

        Assert.Equal(0, client.EnsureStartedCalls);
        Assert.Equal(0, client.TextReadCalls);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_PasswordField_LearnsNothing()
    {
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field, PasswordResult = true
        };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "hunter2";
        await service.ArmAsync("hunter2");

        client.TextToReturn = "hunter3";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
        // The password guard runs before the baseline read, so the field is never read.
        Assert.Equal(0, client.TextReadCalls);
    }

    [Fact]
    public async Task Arm_PasswordRoleIndeterminate_FailsClosed_LearnsNothing()
    {
        // The role read could not be determined (null). A privacy boundary must fail closed:
        // never proceed to read the field text.
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field, PasswordResult = null
        };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "secret value";
        await service.ArmAsync("secret value");

        Assert.Equal(0, client.TextReadCalls);
        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Commit_SingleWordSpellingFix_StillLearns()
    {
        // The similarity gate must NOT break the flagship one-word case (no surrounding context).
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "kubernets";
        await service.ArmAsync("kubernets");

        client.TextToReturn = "Kubernetes";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Commit_LowSimilarityIntentChange_LearnsNothing()
    {
        // A short whole-phrase rewrite ("call mom" -> "email dad") passes the shared word-diff
        // (<=3 tokens), but is a change of intent, not a recognition fix — must not be learned.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "call mom";
        await service.ArmAsync("call mom");

        client.TextToReturn = "email dad";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_IdleCommit_ThenMoreWordsTyped_DoesNotWidenReplacement()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "kubernets";
        await service.ArmAsync("kubernets");

        // Idle fires once the single-word correction is complete → learns kubernets->Kubernetes.
        client.TextToReturn = "Kubernetes";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Kubernetes", Assert.Single(_dictionary.GetCorrections()).Replacement);

        // The user then keeps typing new words. Diffing the unchanged baseline against
        // "Kubernetes now" would widen the replacement — the guard must keep the earlier value.
        client.TextToReturn = "Kubernetes now";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_DisabledDuringStartup_DoesNotReadTargetText()
    {
        // The user disables the feature while ArmAsync is still awaiting EnsureStartedAsync.
        // The opt-out re-check must bail before any field text is read.
        var startGate = new TaskCompletionSource();
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field, StartGate = startGate
        };
        var settings = new FakeSettingsService(
            AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
        );
        using var service = CreateService(client, settings);

        client.TextToReturn = "I deployed to kubernets today";
        var arm = service.ArmAsync("I deployed to kubernets today"); // blocks in EnsureStartedAsync
        await WaitUntilAsync(() => client.EnsureStartedCalls == 1);

        settings.Save(AppSettings.Default with { TargetAppCorrectionLearningEnabled = false });
        startGate.SetResult();
        await arm.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, client.TextReadCalls);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task EnableThenDisable_WhileStartInFlight_EndsStoppedAndSubscribedQuiet()
    {
        // Race: a start reconcile is in flight (blocked mid-EnsureStartedAsync) when the user
        // disables. Serialization must guarantee the stop reconcile runs *after* the start, so
        // the connection ends torn down (StopAsync called) rather than left listening.
        var startGate = new TaskCompletionSource();
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field, StartGate = startGate
        };
        var settings = new FakeSettingsService(
            AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
        );
        using var service = CreateService(client, settings);

        service.Initialize(); // fires the (blocked) start reconcile
        await WaitUntilAsync(() => client.EnsureStartedCalls == 1);

        settings.Save(AppSettings.Default with { TargetAppCorrectionLearningEnabled = false });
        startGate.SetResult(); // let the start finish; the stop reconcile is queued behind it

        var last = service.LastListenTask;
        Assert.NotNull(last);
        await last.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(client.StopCalls >= 1);
    }

    [Fact]
    public async Task Arm_ThenEdit_ThenIdle_LearnsWithoutFocusOut()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(20)
        );

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // User fixes the word but keeps the cursor in the field — no focus-out, only idle.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_PartialIdleCommit_ThenCompleteFinalCommit_SelfHeals()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(20)
        );

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // Idle fires mid-correction: the user has typed all but the final letter
        // ("Kubernete"). This partial is still similar enough to pass the recognition-fix gate,
        // so it is learned and later overwritten.
        client.TextToReturn = "I deployed to Kubernete today";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);

        Assert.Equal("Kubernete", Assert.Single(_dictionary.GetCorrections()).Replacement);

        // The user finishes the word and moves on: the final commit re-diffs and overwrites.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_IdleThenIdenticalFinalCommit_DoesNotInflateCount()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(20)
        );

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // Idle commits the completed correction; the later focus-out commit sees the same text.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);

        client.RaiseFocus(s_otherField);
        await AwaitCommit(service);

        var entry = Assert.Single(
            _dictionary.Entries,
            e => e.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("kubernets", entry.Original);
        Assert.Equal("Kubernetes", entry.Replacement);
        Assert.Equal(1, entry.TimesCorrected);
    }

    [Fact]
    public async Task Arm_ThenEdit_ThenTimeout_LearnsWithoutFocusOut()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            trackingWindow: TimeSpan.FromMilliseconds(30)
        );

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // Edit, then neither focus-out nor idle (idle default 3s) — the tracking window
        // elapses first and its final commit learns the correction.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Disable_MidWindow_StopsClientAndLearnsNothing()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        var settings = new FakeSettingsService(
            AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
        );
        using var service = CreateService(client, settings);
        // Wire settings-change handling (the orchestrator does this on startup).
        service.Initialize();

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // User disables the feature while a tracking window is open.
        settings.Save(AppSettings.Default with { TargetAppCorrectionLearningEnabled = false });
        await WaitUntilAsync(() => client.StopCalls > 0);

        // A subsequent edit + focus-out must not learn anything.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_WhenBusUnavailable_NoOps()
    {
        var client = new FakeAtSpiEventClient { Available = false, CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        Assert.Equal(1, client.EnsureStartedCalls);
        Assert.Equal(0, client.TextReadCalls);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_BaselineNeverContainsInsertedText_SkipsAfterRetries()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        // The read never reflects the inserted text (e.g. still draining, or wrong field):
        // arming must be skipped after exactly three read attempts.
        client.TextToReturn = "totally unrelated field contents";
        await service.ArmAsync("I deployed to kubernets today");

        Assert.Equal(3, client.TextReadCalls);

        // A later edit + focus-out on the (never-armed) field learns nothing.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Theory]
    // Feature on + direct insertion of a normal-length edit → arm.
    [InlineData(true, InsertionResult.Typed, false, 20, true)]
    [InlineData(true, InsertionResult.Pasted, false, 20, true)]
    // Feature off → never arm.
    [InlineData(false, InsertionResult.Typed, false, 20, false)]
    // Clipboard fallback (not a direct insertion) → never arm.
    [InlineData(true, InsertionResult.CopiedToClipboard, false, 20, false)]
    // Action-plugin output (not plain dictation) → never arm.
    [InlineData(true, InsertionResult.Typed, true, 20, false)]
    // Oversized insertion (document dump) → never arm.
    [InlineData(true, InsertionResult.Typed, false, 2049, false)]
    // Empty insertion → never arm.
    [InlineData(true, InsertionResult.Typed, false, 0, false)]
    public void ShouldArm_GatingMatrix(
        bool enabled,
        InsertionResult insertion,
        bool hasActionPlugin,
        int length,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            TargetAppCorrectionLearningService.ShouldArm(enabled, insertion, hasActionPlugin, length)
        );
    }

    private TargetAppCorrectionLearningService CreateService(
        FakeAtSpiEventClient client,
        bool enabled
    )
    {
        return CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = enabled }
            )
        );
    }

    private TargetAppCorrectionLearningService CreateService(
        FakeAtSpiEventClient client,
        FakeSettingsService settings,
        TimeSpan? trackingWindow = null,
        TimeSpan? idleCommitDelay = null
    )
    {
        return new TargetAppCorrectionLearningService(
            client,
            _dictionary,
            settings,
            new NullErrorLogService(),
            trackingWindow ?? TimeSpan.FromSeconds(30),
            idleCommitDelay ?? TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(1)
        );
    }

    private static async Task AwaitCommit(TargetAppCorrectionLearningService service)
    {
        var task = service.LastCommitTask;
        Assert.NotNull(task);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Waits for a background (idle/timeout) commit to be scheduled, then awaits it. Unlike
    // AwaitCommit, the commit here is fired by a timer, so LastCommitTask is null until the
    // callback runs.
    private static async Task AwaitScheduledCommit(TargetAppCorrectionLearningService service)
    {
        await WaitUntilAsync(() => service.LastCommitTask is not null);
        var task = service.LastCommitTask;
        Assert.NotNull(task);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeAtSpiEventClient : IAtSpiEventClient
    {
        public bool Available { get; init; } = true;

        // null models a role read that could not be determined (fail-closed path).
        public bool? PasswordResult { get; init; } = false;
        public string? TextToReturn { get; set; }
        public AtSpiElementRef? CurrentFocusedElement { get; set; }
        public int EnsureStartedCalls { get; private set; }
        public int TextReadCalls { get; private set; }
        public int StopCalls { get; private set; }

        // When set, EnsureStartedAsync blocks on this until released — lets a test hold a start
        // in flight while a disable is issued, to exercise the start/stop serialization.
        public TaskCompletionSource? StartGate { get; init; }

        public event Action<AtSpiElementRef>? FocusChanged;
        public event Action<AtSpiElementRef>? TextChanged;

        public async Task<bool> EnsureStartedAsync()
        {
            EnsureStartedCalls++;
            if (StartGate is not null)
            {
                await StartGate.Task.ConfigureAwait(false);
            }

            return Available;
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength)
        {
            TextReadCalls++;
            return Task.FromResult(TextToReturn);
        }

        public Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
        {
            return Task.FromResult(PasswordResult);
        }

        public void RaiseFocus(AtSpiElementRef element)
        {
            CurrentFocusedElement = element;
            FocusChanged?.Invoke(element);
        }

        public void RaiseText(AtSpiElementRef element)
        {
            TextChanged?.Invoke(element);
        }
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
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }

    private sealed class NullErrorLogService : IErrorLogService
    {
        public IReadOnlyList<ErrorLogEntry> Entries => [];

        public void AddEntry(string message, string category = ErrorCategory.General)
        {
        }

        public void ClearAll()
        {
        }

        public string ExportDiagnostics()
        {
            return string.Empty;
        }

        public event Action? EntriesChanged
        {
            add { }
            remove { }
        }
    }
}
