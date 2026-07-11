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

    // A field in a DIFFERENT application: focus moving here ends tracking. Same-app focus
    // changes deliberately do not (LibreOffice Writer re-asserts focus on structural panes
    // while the user is still editing the armed field).
    private static readonly AtSpiElementRef s_otherAppField = new("other-app", "/field/2");

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
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_ThenFocusOutCommit_ReleasesTextChangedLease()
    {
        // The text-changed registration is what floods the a11y bus, so it must be held only
        // while a window is armed: acquired on arm, released when the final focus-out commit
        // drops the state. A leak here silently reinstates the permanent flood.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");
        Assert.Equal(1, client.AcquireCount);
        Assert.Equal(1, client.ActiveAcquisitions);

        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task Arm_ThenTimeout_ReleasesTextChangedLease()
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
        Assert.Equal(1, client.ActiveAcquisitions);

        // Edit, then let the tracking window elapse (its final commit drops the state).
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);

        await WaitUntilAsync(() => client.ActiveAcquisitions == 0);
        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task Arm_ThenTimeoutWithoutEdit_ReleasesTextChangedLease()
    {
        // A timeout with no intervening edit is a FINAL commit down the "nothing to learn" path;
        // it must still release the lease.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            trackingWindow: TimeSpan.FromMilliseconds(30)
        );

        client.TextToReturn = "hello world";
        await service.ArmAsync("hello world");
        Assert.Equal(1, client.ActiveAcquisitions);

        await WaitUntilAsync(() => client.ActiveAcquisitions == 0);
        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task ReArm_ReleasesPreviousLease_AndHoldsExactlyOne()
    {
        // A second arm while the first is still armed must dispose the old state's lease before
        // acquiring the new one — two acquisitions total, exactly one live.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");
        Assert.Equal(1, client.AcquireCount);
        Assert.Equal(1, client.ActiveAcquisitions);

        // Re-arm without any commit/focus-out in between.
        client.TextToReturn = "a fresh unrelated dictation";
        await service.ArmAsync("a fresh unrelated dictation");

        Assert.Equal(2, client.AcquireCount);
        Assert.Equal(1, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task Disable_MidWindow_ReleasesTextChangedLease()
    {
        // Disabling the feature disarms via the reconcile; the lease must be released so the
        // registration drops when the process stops receiving a11y traffic.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        var settings = new FakeSettingsService(
            AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
        );
        using var service = CreateService(client, settings);
        service.Initialize();

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");
        Assert.Equal(1, client.ActiveAcquisitions);

        settings.Save(AppSettings.Default with { TargetAppCorrectionLearningEnabled = false });
        await WaitUntilAsync(() => client.ActiveAcquisitions == 0);

        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task Dispose_WhileArmed_ReleasesTextChangedLease()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");
        Assert.Equal(1, client.ActiveAcquisitions);

        service.Dispose();

        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task Arm_FallsBackToRecentSameAppElement_WhenFocusedElementHasNoText()
    {
        // LibreOffice Writer: the document's root pane (no Text interface) is the most
        // recent focused element, while the caret paragraph the dictation landed in sits
        // one entry back in the focus history.
        var pane = new AtSpiElementRef("app", "/pane");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = pane,
            TextProvider = e => e.Equals(s_field) ? "I deployed to kubernets today" : null
        };
        client.RecentFocusedElements.AddRange([pane, s_field]);
        using var service = CreateService(client, enabled: true);

        await service.ArmAsync("I deployed to kubernets today");

        client.TextProvider = e => e.Equals(s_field) ? "I deployed to Kubernetes today" : null;
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_IgnoresRecentElementsFromOtherApps()
    {
        // The fallback must never read a stale field that belongs to a different
        // application, even if that field happens to contain the inserted text.
        var pane = new AtSpiElementRef("app", "/pane");
        var foreignField = new AtSpiElementRef("foreign-app", "/field/1");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = pane,
            TextProvider = e => e.Equals(foreignField) ? "hello world" : null
        };
        client.RecentFocusedElements.AddRange([pane, foreignField]);
        using var service = CreateService(client, enabled: true);

        await service.ArmAsync("hello world");

        // Arming skipped, so an edit in the foreign field must not be learned.
        client.TextProvider = e => e.Equals(foreignField) ? "hello there world" : null;
        client.RaiseText(foreignField);
        client.RaiseFocus(s_otherAppField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task SameAppFocusChange_DoesNotEndTracking()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        // LibreOffice Writer re-asserts focus on the document's root pane between
        // keystrokes; that must not end tracking of the armed paragraph.
        client.RaiseFocus(new AtSpiElementRef("app", "/pane"));

        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_PokesAccessibilityTrees_ToUnlockChromiumApps()
    {
        // Chromium/Electron apps expose a stub tree until an AT touches it; every arm must
        // fire the unlock sweep so apps launched after the client started become readable.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "hello world";
        await service.ArmAsync("hello world");

        Assert.True(client.PokeCalls >= 1);
    }

    [Fact]
    public async Task Arm_FocusOutWithoutEdit_LearnsNothing()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "hello world";
        await service.ArmAsync("hello world");

        // Focus leaves without any text-changed event. The final commit still reads the field as a
        // fallback (event registration is best-effort), but the text is unchanged from the baseline,
        // so nothing is learned.
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_FocusOutAfterMissedEvent_StillLearns()
    {
        // The user corrected the field but no text-changed event arrived (registration lagged the
        // arm, or the toolkit didn't emit). Edited stays false, yet the final focus-out commit reads
        // the field, diffs it against the baseline, and learns the edit anyway — the fallback that
        // keeps best-effort events from silently losing a correction.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "kubernets";
        await service.ArmAsync("kubernets");

        // Field changes, but RaiseText is deliberately NOT called — no event is delivered.
        client.TextToReturn = "Kubernetes";
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
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
        client.RaiseFocus(s_otherAppField);
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
        client.RaiseFocus(s_otherAppField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
        // The password guard runs before the baseline read, so the field is never read.
        Assert.Equal(0, client.TextReadCalls);
    }

    [Fact]
    public async Task Arm_FocusedPasswordField_DoesNotFallBackToSibling()
    {
        // The dictation landed in a password field, but a same-app sibling happens to contain
        // the same text. Falling back to it would leak password-derived text into learned
        // corrections, so the whole arm must abort — and no field text may ever be read.
        var password = new AtSpiElementRef("app", "/password");
        var sibling = new AtSpiElementRef("app", "/sibling");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = password,
            PasswordProvider = e => e.Equals(password),
            TextProvider = _ => "hunter2"
        };
        client.RecentFocusedElements.AddRange([password, sibling]);
        using var service = CreateService(client, enabled: true);

        await service.ArmAsync("hunter2");

        // Not armed: an edit in the sibling must not be learned, and no text was ever read.
        client.TextProvider = e => e.Equals(sibling) ? "hunter2 corrected" : "hunter2";
        client.RaiseText(sibling);
        client.RaiseFocus(s_otherAppField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
        Assert.Equal(0, client.TextReadCalls);
    }

    [Fact]
    public async Task Arm_IndeterminateFocusedRole_DoesNotFallBackToSibling()
    {
        // The focused element's role could not be read — it could itself be a password
        // field, so the whole arm must abort without probing same-app siblings.
        var pane = new AtSpiElementRef("app", "/pane");
        var sibling = new AtSpiElementRef("app", "/sibling");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = pane,
            PasswordProvider = e => e.Equals(pane) ? null : false,
            TextProvider = e => e.Equals(sibling) ? "hello world" : null
        };
        client.RecentFocusedElements.AddRange([pane, sibling]);
        using var service = CreateService(client, enabled: true);

        await service.ArmAsync("hello world");

        Assert.Equal(0, client.TextReadCalls);
        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Commit_ElementBecomesPasswordDuringTracking_LearnsNothing()
    {
        // The role can change (or the object path be recycled) between arming and the
        // commit up to a full tracking window later; the final read must re-verify it.
        var isPassword = false;
        // ReSharper disable once AccessToModifiedClosure -- the flip mid-test is the point.
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field,
            PasswordProvider = _ => isPassword ? true : (bool?)false
        };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");

        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);

        isPassword = true;
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
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
        client.RaiseFocus(s_otherAppField);
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
        client.RaiseFocus(s_otherAppField);
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
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_TwoAdjacentWordsCorrectedInSequence_LearnsThemSeparately()
    {
        // Regression: fix one misrecognized name (an idle commit learns it), then fix the adjacent
        // name. The second commit re-diffs the unchanged baseline, so BOTH words now differ from it
        // — without per-word splitting they fused into one "Chris kharrington" -> "Curris
        // Carrington" entry.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "Kim Chris kharrington Quinn";
        await service.ArmAsync("Kim Chris kharrington Quinn");

        // Fix the second name first; an idle commit learns kharrington -> Carrington.
        client.TextToReturn = "Kim Chris Carrington Quinn";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Carrington", Assert.Single(_dictionary.GetCorrections()).Replacement);

        // Now fix the first name and move on. The diff against the original baseline shows both
        // names changed, but only the genuinely new edit should be learned.
        client.TextToReturn = "Kim Curris Carrington Quinn";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var corrections = _dictionary.GetCorrections();
        Assert.Equal(2, corrections.Count);
        Assert.Contains(corrections, c => c is { Original: "Chris", Replacement: "Curris" });
        Assert.Contains(corrections, c => c is { Original: "kharrington", Replacement: "Carrington" });
        // The fused multi-word entry must never be created.
        Assert.DoesNotContain(corrections, c => c.Original.Contains(' '));
    }

    [Fact]
    public async Task Arm_LearnedAndNewEditSeparatedByUnchangedWord_LearnsOnlyTheNewWord()
    {
        // After an idle commit learns kharrington -> Carrington, the user fixes a second word with
        // an UNCHANGED word ("in") between them. The re-diff spans all three, but the unchanged
        // anchor must be dropped: we learn smyth -> Smith, not "in smyth" -> "in Smith", and never
        // a no-op "in" -> "in".
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "please note kharrington in smyth right here";
        await service.ArmAsync("please note kharrington in smyth right here");

        client.TextToReturn = "please note Carrington in smyth right here";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Carrington", Assert.Single(_dictionary.GetCorrections()).Replacement);

        client.TextToReturn = "please note Carrington in Smith right here";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var corrections = _dictionary.GetCorrections();
        Assert.Equal(2, corrections.Count);
        Assert.Contains(corrections, c => c is { Original: "kharrington", Replacement: "Carrington" });
        Assert.Contains(corrections, c => c is { Original: "smyth", Replacement: "Smith" });
        Assert.DoesNotContain(corrections, c => c.Original.Contains(' '));
        Assert.DoesNotContain(corrections, c => c.Original == c.Replacement);
    }

    [Fact]
    public async Task Arm_MergeFixAdjacentToLearnedWord_DropsLearnedWordFromPhrase()
    {
        // After an idle commit learns kharrington -> Carrington, the user makes an adjacent
        // merge/split fix ("type whisper" -> "TypeWhisper"). The re-diff fuses them into unequal
        // token counts; the settled word must still be trimmed off the edge so the surviving pair
        // is the clean merge "type whisper" -> "TypeWhisper", never
        // "type whisper kharrington" -> "TypeWhisper Carrington". That merge's original still
        // contains a space, so the Core token-safety filter rejects it — only the earlier
        // single-word correction persists. The point here is that de-fusing produced the clean pair.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "please open type whisper kharrington now thanks";
        await service.ArmAsync("please open type whisper kharrington now thanks");

        client.TextToReturn = "please open type whisper Carrington now thanks";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Carrington", Assert.Single(_dictionary.GetCorrections()).Replacement);

        client.TextToReturn = "please open TypeWhisper Carrington now thanks";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        // The fused "type whisper kharrington" -> "TypeWhisper Carrington" entry must never exist.
        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kharrington", correction.Original);
        Assert.Equal("Carrington", correction.Replacement);
    }

    [Fact]
    public async Task Arm_MergeFixSeparatedFromLearnedWordByConnector_TrimsConnector()
    {
        // Like the merge/split case, but an unchanged connector ("in") sits between the merge fix
        // and the learned edge word. Trimming the learned word exposes that connector at the edge
        // of an unequal remainder; it must be trimmed too, so the surviving pair is the clean
        // "type whisper" -> "TypeWhisper", never "type whisper in" -> "TypeWhisper in". That pair's
        // original still contains a space, so the Core token-safety filter rejects it — only the
        // earlier single-word correction persists, and no re-fused multi-word entry is ever created.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "please open type whisper in kharrington now thanks";
        await service.ArmAsync("please open type whisper in kharrington now thanks");

        client.TextToReturn = "please open type whisper in Carrington now thanks";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Carrington", Assert.Single(_dictionary.GetCorrections()).Replacement);

        client.TextToReturn = "please open TypeWhisper in Carrington now thanks";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kharrington", correction.Original);
        Assert.Equal("Carrington", correction.Replacement);
    }

    [Fact]
    public async Task Arm_EqualLengthPhraseEdit_StaysAtomic_ThenRejectedAsUnsafeMultiWord()
    {
        // De-fusing keeps a same-length multi-word edit atomic (never split into per-word rules
        // that could rewrite unrelated future text), so the batch carries ONE
        // "kubernets cluster" -> "Kubernetes clusters" pair. The Core batch API then rejects it:
        // its token-safety filter forbids a space-containing original/replacement, so silent
        // auto-learn can no longer persist a multi-word phrase. Nothing is learned.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "deploy kubernets cluster";
        await service.ArmAsync("deploy kubernets cluster");

        client.TextToReturn = "deploy Kubernetes clusters";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_PhraseEditWithUnchangedConnector_StaysAtomic_ThenRejectedAsUnsafeMultiWord()
    {
        // Same as above but with an unchanged connector word ("in") inside the changed span. The
        // connector must NOT act as a split point (de-fusing keeps the phrase atomic, never per-word
        // rules like "cluster" -> "clusters"), so the batch carries ONE
        // "kubernets in cluster" -> "Kubernetes in clusters" pair — which the Core token-safety
        // filter then rejects as a space-containing multi-word token. Nothing is learned.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "we deploy kubernets in cluster now";
        await service.ArmAsync("we deploy kubernets in cluster now");

        client.TextToReturn = "we deploy Kubernetes in clusters now";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_LearnedWordAdjacentToFreshPhrase_KeepsPhraseAtomic()
    {
        // A learned word (kharrington -> Carrington) is de-fused off the span, but the adjacent NEW
        // edit is itself a phrase with an unchanged connector ("in"). De-fusing must not also split
        // that phrase into per-word rules ("kubernets" -> "Kubernetes" / "cluster" -> "clusters"):
        // it stays the single atomic "kubernets in cluster" -> "Kubernetes in clusters" pair, which
        // the Core token-safety filter then rejects as multi-word. So only kharrington -> Carrington
        // persists; the fact that no "kubernets"/"cluster" per-word entries appear proves the phrase
        // was kept atomic (had it been split, both are safe single-word tokens and WOULD be learned).
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(50)
        );

        client.TextToReturn = "the note about kharrington kubernets in cluster is here";
        await service.ArmAsync("the note about kharrington kubernets in cluster is here");

        client.TextToReturn = "the note about Carrington kubernets in cluster is here";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        Assert.Equal("Carrington", Assert.Single(_dictionary.GetCorrections()).Replacement);

        client.TextToReturn = "the note about Carrington Kubernetes in clusters is here";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kharrington", correction.Original);
        Assert.Equal("Carrington", correction.Replacement);
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
        client.RaiseFocus(s_otherAppField);
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

        client.RaiseFocus(s_otherAppField);
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
        client.RaiseFocus(s_otherAppField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Disable_BetweenArmAndCommit_DoesNotReadOrLearn()
    {
        // The tracking window is open (armed + edited) when the user disables the feature, but
        // the listener reconcile has not disarmed yet (Initialize is not wired here, so flipping
        // the setting does not run StopAsync/Disarm). The opt-out re-check in CommitAsync must
        // bail before reading the field again or learning.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        var settings = new FakeSettingsService(
            AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
        );
        using var service = CreateService(client, settings);

        client.TextToReturn = "I deployed to kubernets today";
        await service.ArmAsync("I deployed to kubernets today");
        Assert.Equal(1, client.TextReadCalls); // baseline read only

        // The user fixes the word (arming the edit), then disables before focus-out commits it.
        client.TextToReturn = "I deployed to Kubernetes today";
        client.RaiseText(s_field);
        settings.Save(AppSettings.Default with { TargetAppCorrectionLearningEnabled = false });

        // Focus-out schedules a final commit; the opt-out guard must stop it before the read.
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Equal(1, client.TextReadCalls); // no second read after opt-out
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
        client.RaiseFocus(s_otherAppField);

        Assert.Null(service.LastCommitTask);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_EditOutsideDictatedSpan_LearnsNothing()
    {
        // The field already holds text around the dictated span. The user edits the PREFIX
        // (unrelated to the dictation), leaving the dictated span untouched. Confining the diff to
        // the inserted span means that unrelated edit is not learned as a "correction".
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "PREFIX kubernets SUFFIX";
        await service.ArmAsync("kubernets");

        // Edit only the surrounding prefix; the dictated word "kubernets" is unchanged.
        client.TextToReturn = "CHANGED kubernets SUFFIX";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_EditInsideDictatedSpanWithSurroundingText_LearnsCorrection()
    {
        // Surrounding text is present, but the user edits INSIDE the dictated span. The span-
        // confined diff still finds and learns the correction.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "PREFIX kubernets SUFFIX";
        await service.ArmAsync("kubernets");

        client.TextToReturn = "PREFIX Kubernetes SUFFIX";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var correction = Assert.Single(_dictionary.GetCorrections());
        Assert.Equal("kubernets", correction.Original);
        Assert.Equal("Kubernetes", correction.Replacement);
    }

    [Fact]
    public async Task Arm_InsertedTextOccursTwiceInBaseline_LearnsNothing()
    {
        // The inserted text appears twice back-to-back in the field, so an edit between the two
        // occurrences validates against EITHER framing — the edited span is ambiguous (we can't
        // tell which occurrence the user was correcting). Span extraction returns null when more
        // than one occurrence matches, so nothing is learned.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "kubernetskubernets";
        await service.ArmAsync("kubernets");

        // Insert text between the two occurrences: both "" +suffix and prefix+ "" framings hold.
        client.TextToReturn = "kubernetsXkubernets";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_EditToPreExistingDuplicateCopy_LearnsNothing()
    {
        // The field already holds "form"; the dictation inserts a second "form" (baseline
        // "form form"). The user then fixes the PRE-EXISTING first copy to "from" ("from form").
        // Only one framing validates, but because the inserted text occurs twice we can't attribute
        // the edit to the dictated copy — so nothing is learned (else we'd wrongly learn
        // "form" -> "from" from an edit the dictation never touched).
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "form form";
        await service.ArmAsync("form");

        client.TextToReturn = "from form";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_EditToPreExistingCaseVariantCopy_LearnsNothing()
    {
        // The field already holds "form"; the dictated "form" is autocapitalized by the app to
        // "Form" (baseline "form Form"). Arming anchors case-insensitively, so an Ordinal-only
        // duplicate check would miss the pre-existing lowercase copy. Editing that first copy to
        // "from" ("from Form") must learn nothing — else we'd wrongly learn "form" -> "from" from
        // a copy the dictation never touched.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        client.TextToReturn = "form Form";
        await service.ArmAsync("form");

        client.TextToReturn = "from Form";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Commit_RaisesCorrectionsLearnedEvent_WithLearnedEntryAndSourceExtents()
    {
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = s_field,
            ExtentsToReturn = new AtSpiScreenRect(100, 200, 300, 40)
        };
        using var service = CreateService(client, enabled: true);

        LearnedCorrectionsBatch? raised = null;
        service.CorrectionsLearned += batch => raised = batch;

        client.TextToReturn = "kubernets";
        await service.ArmAsync("kubernets");

        client.TextToReturn = "Kubernetes";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.NotNull(raised);
        var learned = Assert.Single(raised.Corrections);
        Assert.Equal("kubernets", learned.Original);
        Assert.Equal("Kubernetes", learned.Replacement);
        // The event carries the same id the dictionary stored, so a subscriber can undo it.
        var entry = Assert.Single(
            _dictionary.Entries,
            e => e.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal(entry.Id, learned.Id);
        // The corrected element's screen box rides along so the toast can be placed beside it.
        Assert.Equal(new AtSpiScreenRect(100, 200, 300, 40), raised.SourceExtents);
    }

    [Fact]
    public async Task Commit_LearnsNothing_DoesNotRaiseCorrectionsLearnedEvent()
    {
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(client, enabled: true);

        var raisedCount = 0;
        service.CorrectionsLearned += _ => raisedCount++;

        client.TextToReturn = "call mom";
        await service.ArmAsync("call mom");

        // A change of intent is rejected downstream, so no entry is learned and no event fires.
        client.TextToReturn = "email dad";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        Assert.Equal(0, raisedCount);
        Assert.Empty(_dictionary.GetCorrections());
    }

    [Fact]
    public async Task Arm_IdleCommit_ThenFinalCommit_SelfHealsSameEntry()
    {
        // An idle (non-final) commit learns a partial correction into one dictionary entry; the
        // final commit re-diffs and self-heals — updating that SAME entry rather than adding a new
        // one — because the batch call passes the session's entry id as replaceable.
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = s_field };
        using var service = CreateService(
            client,
            new FakeSettingsService(
                AppSettings.Default with { TargetAppCorrectionLearningEnabled = true }
            ),
            idleCommitDelay: TimeSpan.FromMilliseconds(20)
        );

        client.TextToReturn = "kubernets";
        await service.ArmAsync("kubernets");

        client.TextToReturn = "Kubernete";
        client.RaiseText(s_field);
        await AwaitScheduledCommit(service);
        var afterIdle = Assert.Single(
            _dictionary.Entries,
            e => e.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("Kubernete", afterIdle.Replacement);
        var idleId = afterIdle.Id;

        client.TextToReturn = "Kubernetes";
        client.RaiseText(s_field);
        client.RaiseFocus(s_otherAppField);
        await AwaitCommit(service);

        var afterFinal = Assert.Single(
            _dictionary.Entries,
            e => e.EntryType == DictionaryEntryType.Correction
        );
        Assert.Equal("Kubernetes", afterFinal.Replacement);
        // Same entry id: the final commit healed the idle-created entry, it did not add a second.
        Assert.Equal(idleId, afterFinal.Id);
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

        // Per-element text for candidate-fallback tests; when null, TextToReturn is used
        // for every element.
        public Func<AtSpiElementRef, string?>? TextProvider { get; set; }

        // Per-element password verdict for candidate-fallback tests; when null, PasswordResult
        // is used for every element.
        public Func<AtSpiElementRef, bool?>? PasswordProvider { get; init; }
        public AtSpiElementRef? CurrentFocusedElement { get; set; }
        public List<AtSpiElementRef> RecentFocusedElements { get; } = [];
        public int EnsureStartedCalls { get; private set; }
        public int TextReadCalls { get; private set; }
        public int StopCalls { get; private set; }

        // When set, EnsureStartedAsync blocks on this until released — lets a test hold a start
        // in flight while a disable is issued, to exercise the start/stop serialization.
        public TaskCompletionSource? StartGate { get; init; }

        public event Action<AtSpiElementRef>? FocusChanged;
        public event Action<AtSpiElementRef>? TextChanged;

        public bool IsRunning { get; private set; }

        // Total AcquireTextChangedEvents calls, and how many of those leases are still undisposed.
        // Tests assert acquire/release pairing: ActiveAcquisitions must return to 0 on every path
        // that drops the armed state, so a leaked lease (which would reinstate the a11y flood) fails.
        public int AcquireCount { get; private set; }
        public int ActiveAcquisitions { get; private set; }

        public IDisposable AcquireTextChangedEvents()
        {
            AcquireCount++;
            ActiveAcquisitions++;
            return new Lease(this);
        }

        public async Task<bool> EnsureStartedAsync()
        {
            EnsureStartedCalls++;
            if (StartGate is not null)
            {
                await StartGate.Task.ConfigureAwait(false);
            }

            IsRunning = Available;
            return Available;
        }

        public Task StopAsync()
        {
            StopCalls++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements()
        {
            return [.. RecentFocusedElements];
        }

        public int PokeCalls { get; private set; }

        public Task PokeAccessibilityTreesAsync()
        {
            PokeCalls++;
            return Task.CompletedTask;
        }

        public Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength)
        {
            TextReadCalls++;
            return Task.FromResult(TextProvider is not null ? TextProvider(element) : TextToReturn);
        }

        public Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
        {
            return Task.FromResult(PasswordProvider is not null ? PasswordProvider(element) : PasswordResult);
        }

        // On-screen box returned for the corrected element; null models an app that doesn't
        // expose the Component interface (the common native-Wayland case).
        public AtSpiScreenRect? ExtentsToReturn { get; init; }

        public Task<AtSpiScreenRect?> TryGetScreenExtentsAsync(AtSpiElementRef element)
        {
            return Task.FromResult(ExtentsToReturn);
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

        // Idempotent, mirroring the real handle: a double-dispose must not double-decrement.
        private sealed class Lease(FakeAtSpiEventClient owner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.ActiveAcquisitions--;
            }
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
