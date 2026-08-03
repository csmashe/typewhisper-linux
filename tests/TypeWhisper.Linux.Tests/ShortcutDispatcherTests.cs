using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ShortcutDispatcherTests
{
    [Fact]
    public void PromptAction_WaitsForTriggerAndAllModifiers_ThenFiresWithId()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        string? observed = null;
        var count = 0;
        d.PromptActionRequested += id =>
        {
            observed = id;
            count++;
        };

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        Assert.Null(observed);
        Assert.Equal(0, count);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, false);
        Assert.Equal(0, count);

        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        Assert.Equal(0, count);

        d.Handle(KeyCode.VcLeftControl, ModifierMask.CapsLock, false);

        Assert.Equal("alpha", observed);
        Assert.Equal(1, count);
    }

    [Fact]
    public void PromptAction_ShortcutsReplacedMidHold_PreservesPayloadAndLaterBinding()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        var observed = new List<string>();
        d.PromptActionRequested += observed.Add;

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        Assert.Empty(observed);

        d.UpdateShortcuts(
            SetWithPromptAction(
                "beta",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, false);
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);

        Assert.Equal(["alpha"], observed);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        Assert.Equal(["alpha"], observed);

        d.Handle(KeyCode.VcR, ModifierMask.None, false);

        Assert.Equal(["alpha", "beta"], observed);
    }

    [Fact]
    public void PromptActionRepeatedPress_DedupsUntilGatedDispatch()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        var count = 0;
        d.PromptActionRequested += _ => count++;

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        Assert.Equal(0, count);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, false);
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);
        Assert.Equal(1, count);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.None, false);

        Assert.Equal(2, count);
    }

    [Fact]
    public void TogglePress_FiresToggle_NotStart()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        int toggle = 0,
            start = 0;
        d.DictationToggleRequested += () => toggle++;
        d.DictationStartRequested += () => start++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(1, toggle);
        Assert.Equal(0, start);
    }

    [Fact]
    public void PushToTalkPressAndRelease_FiresStartThenStop()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.PushToTalk));
        int start = 0,
            stop = 0;
        d.DictationStartRequested += () => start++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(1, start);
        Assert.Equal(1, stop);
    }

    [Fact]
    public void PushToTalk_BindingReplacedDuringHold_ReleasesOldKeyAndAcceptsNewBinding()
    {
        var d = new ShortcutDispatcher();
        var set = Set(RecordingMode.PushToTalk);
        d.UpdateShortcuts(set);
        var start = 0;
        var stop = 0;
        d.DictationStartRequested += () => start++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        Assert.Equal(1, start);

        d.UpdateShortcuts(set with { DictationKey = KeyCode.VcD });
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(1, stop);

        d.Handle(KeyCode.VcD, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcD, ModifierMask.None, false);

        Assert.Equal(2, start);
        Assert.Equal(2, stop);
    }

    [Fact]
    public void PushToTalk_ModeReplacedDuringHold_StopsUsingPressTimeMode()
    {
        var d = new ShortcutDispatcher();
        var set = Set(RecordingMode.PushToTalk);
        d.UpdateShortcuts(set);
        var toggle = 0;
        var start = 0;
        var stop = 0;
        d.DictationToggleRequested += () => toggle++;
        d.DictationStartRequested += () => start++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.UpdateShortcuts(set with { Mode = RecordingMode.Toggle });
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(0, toggle);
        Assert.Equal(1, start);
        Assert.Equal(1, stop);
    }

    [Fact]
    public void HybridShortPress_DoesNotFireStopOnRelease()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Hybrid));
        int toggle = 0,
            stop = 0;
        d.DictationToggleRequested += () => toggle++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);

        Assert.Equal(1, toggle);
        Assert.Equal(0, stop);
    }

    [Fact]
    public void ResetState_WhilePushToTalkHeld_FiresDiscard()
    {
        // Session lock closes the input fd while the dictation key is held: no release event
        // will arrive, so ResetState must emit the discard itself.
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.PushToTalk));
        var start = 0;
        var discard = 0;
        var stop = 0;
        d.DictationStartRequested += () => start++;
        d.DictationDiscardRequested += () => discard++;
        d.DictationStopRequested += () => stop++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.ResetState();

        Assert.Equal(1, start);
        Assert.Equal(1, discard);
        Assert.Equal(0, stop);
    }

    [Fact]
    public void ResetState_AfterToggleRecordingStarted_FiresDiscard()
    {
        // Toggle recording stays active after the key is released, so ResetState must emit the
        // (idempotent) discard unconditionally rather than keying off held-key state.
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var discard = 0;
        d.DictationDiscardRequested += () => discard++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.None, false);
        d.ResetState();

        Assert.Equal(1, discard);
    }

    [Fact]
    public void OsAutoRepeat_DoesNotDoubleFire()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var toggle = 0;
        d.DictationToggleRequested += () => toggle++;

        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(1, toggle);
    }

    [Fact]
    public void Escape_FiresCancelOnlyWhenEnabled()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var cancel = 0;
        d.CancelRequested += () => cancel++;

        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);
        Assert.Equal(0, cancel);

        d.Handle(KeyCode.VcEscape, ModifierMask.None, false);
        d.UpdateShortcuts(Set(RecordingMode.Toggle, true));
        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);
        Assert.Equal(1, cancel);
    }

    [Fact]
    public void Palette_WaitsForModifiersReleasedBeforeTrigger()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var palette = 0;
        d.PromptPaletteRequested += () => palette++;

        d.Handle(KeyCode.VcP, ModifierMask.LeftCtrl, true);
        Assert.Equal(0, palette);

        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);
        Assert.Equal(0, palette);

        d.Handle(KeyCode.VcP, ModifierMask.CapsLock | ModifierMask.NumLock, false);

        Assert.Equal(1, palette);
    }

    [Fact]
    public void TransformSelection_WaitsForTriggerAndCtrlShiftAltMetaReleased()
    {
        const ModifierMask allShortcutModifiers =
            ModifierMask.LeftCtrl
            | ModifierMask.LeftShift
            | ModifierMask.LeftAlt
            | ModifierMask.LeftMeta;
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            Set(RecordingMode.Toggle) with
            {
                TransformSelectionKey = KeyCode.VcT,
                TransformSelectionModifiers = allShortcutModifiers
            }
        );
        var transform = 0;
        d.TransformSelectionRequested += () => transform++;

        d.Handle(KeyCode.VcT, allShortcutModifiers, true);
        Assert.Equal(0, transform);

        d.Handle(KeyCode.VcT, allShortcutModifiers, false);
        d.Handle(
            KeyCode.VcLeftControl,
            ModifierMask.LeftShift | ModifierMask.LeftAlt | ModifierMask.LeftMeta,
            false
        );
        d.Handle(
            KeyCode.VcLeftShift,
            ModifierMask.LeftAlt | ModifierMask.LeftMeta,
            false
        );
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftMeta, false);
        Assert.Equal(0, transform);

        d.Handle(KeyCode.VcLeftMeta, ModifierMask.None, false);

        Assert.Equal(1, transform);
    }

    [Fact]
    public void ResetState_DropsPendingTransformSelection()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            Set(RecordingMode.Toggle) with
            {
                TransformSelectionKey = KeyCode.VcT,
                TransformSelectionModifiers = ModifierMask.LeftAlt
            }
        );
        var transform = 0;
        d.TransformSelectionRequested += () => transform++;

        d.Handle(KeyCode.VcT, ModifierMask.LeftAlt, true);
        Assert.Equal(0, transform);

        d.ResetState();
        d.Handle(KeyCode.VcT, ModifierMask.None, false);

        Assert.Equal(0, transform);
    }

    [Fact]
    public void ClearShortcuts_DropsPendingWorkflow_OnlyReboundActionFiresAfterReRegister()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithPromptAction(
                "alpha",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );
        var observed = new List<string>();
        d.PromptActionRequested += observed.Add;

        // Press queues "alpha", then unregister (clear) before the release that would dispatch it.
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.ClearShortcuts();

        // Re-register the same physical key to a different action.
        d.UpdateShortcuts(
            SetWithPromptAction(
                "beta",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt
            )
        );

        // A fresh press/release chord must fire the rebound action, not the stale "alpha".
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, false);
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);

        Assert.Equal(["beta"], observed);
    }

    [Fact]
    public void ClearShortcuts_DropsHeldDictationGuard_ReboundKeyStillTogglesAfterReRegister()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle));
        var toggles = 0;
        d.DictationToggleRequested += () => toggles++;

        // Unregister mid-hold: the matching release never reaches the dispatcher.
        d.Handle(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        Assert.Equal(1, toggles);
        d.ClearShortcuts();

        // Re-register with a different dictation key; its first press must not be swallowed
        // by the held-key guard left behind by the unregistered binding.
        d.UpdateShortcuts(Set(RecordingMode.Toggle) with { DictationKey = KeyCode.VcD });
        d.Handle(KeyCode.VcD, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(2, toggles);
    }

    [Fact]
    public void ClearShortcuts_DropsCancelGuard_ReboundCancelStillFiresAfterReRegister()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(Set(RecordingMode.Toggle, true));
        var cancels = 0;
        d.CancelRequested += () => cancels++;

        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);
        Assert.Equal(1, cancels);
        d.ClearShortcuts();

        d.UpdateShortcuts(Set(RecordingMode.Toggle, true));
        d.Handle(KeyCode.VcEscape, ModifierMask.None, true);

        Assert.Equal(2, cancels);
    }

    [Fact]
    public void SelectionWorkflows_SharingOneKeyUnderDifferentModifiers_BothDispatch()
    {
        var d = new ShortcutDispatcher();
        // Ctrl+Alt+R runs "alpha"; the palette answers to Ctrl+R on the same physical key.
        var set = SetWithPromptAction(
            "alpha",
            KeyCode.VcR,
            ModifierMask.LeftCtrl | ModifierMask.LeftAlt
        );
        d.UpdateShortcuts(
            set with { PromptPaletteKey = KeyCode.VcR, PromptPaletteModifiers = ModifierMask.LeftCtrl }
        );
        var observed = new List<string>();
        d.PromptActionRequested += observed.Add;
        d.PromptPaletteRequested += () => observed.Add("palette");

        // Claim the prompt action, release only its Alt modifier, then claim the palette on
        // the same key before every modifier is up.
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, false);
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        Assert.Empty(observed);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);

        Assert.Equal(2, observed.Count);
        Assert.Contains("alpha", observed);
        Assert.Contains("palette", observed);
    }

    [Fact]
    public void SelectionWorkflows_AutoRepeatAfterDroppingAModifier_ClaimsOnlyTheFirstWorkflow()
    {
        var d = new ShortcutDispatcher();
        var set = SetWithPromptAction(
            "alpha",
            KeyCode.VcR,
            ModifierMask.LeftCtrl | ModifierMask.LeftAlt
        );
        d.UpdateShortcuts(
            set with { PromptPaletteKey = KeyCode.VcR, PromptPaletteModifiers = ModifierMask.LeftCtrl }
        );
        var observed = new List<string>();
        d.PromptActionRequested += observed.Add;
        d.PromptPaletteRequested += () => observed.Add("palette");

        // R stays down throughout; releasing Alt makes the auto-repeat presses match the palette.
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt, true);
        d.Handle(KeyCode.VcLeftAlt, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl, true);
        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl, true);

        d.Handle(KeyCode.VcR, ModifierMask.LeftCtrl, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.None, false);

        Assert.Equal(["alpha"], observed);
    }

    [Fact]
    public void ProfileStartDictation_Toggle_FiresToggleWithId()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithProfileHotkey(
                "email",
                KeyCode.VcE,
                ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                ProfileHotkeyBehavior.StartDictation
            )
        );
        string? toggled = null;
        var toggleCount = 0;
        var startCount = 0;
        d.ProfileDictationToggleRequested += id =>
        {
            toggled = id;
            toggleCount++;
        };
        d.ProfileDictationStartRequested += _ => startCount++;

        d.Handle(KeyCode.VcE, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal("email", toggled);
        Assert.Equal(1, toggleCount);
        Assert.Equal(0, startCount);
    }

    [Fact]
    public void ProfileStartDictation_PushToTalk_FiresStartThenStop()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithProfileHotkey(
                "email",
                KeyCode.VcE,
                ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                ProfileHotkeyBehavior.StartDictation,
                RecordingMode.PushToTalk
            )
        );
        string? started = null;
        var startCount = 0;
        var stopCount = 0;
        d.ProfileDictationStartRequested += id =>
        {
            started = id;
            startCount++;
        };
        d.ProfileDictationStopRequested += () => stopCount++;

        d.Handle(KeyCode.VcE, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcE, ModifierMask.None, false);

        Assert.Equal("email", started);
        Assert.Equal(1, startCount);
        Assert.Equal(1, stopCount);
    }

    [Fact]
    public void ProfileStartDictation_HybridShortPress_DoesNotStopOnRelease()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithProfileHotkey(
                "email",
                KeyCode.VcE,
                ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                ProfileHotkeyBehavior.StartDictation,
                RecordingMode.Hybrid
            )
        );
        var toggleCount = 0;
        var stopCount = 0;
        d.ProfileDictationToggleRequested += _ => toggleCount++;
        d.ProfileDictationStopRequested += () => stopCount++;

        d.Handle(KeyCode.VcE, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcE, ModifierMask.None, false);

        Assert.Equal(1, toggleCount);
        Assert.Equal(0, stopCount);
    }

    [Fact]
    public void ProfileStartDictation_OsAutoRepeat_DoesNotDoubleFire()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithProfileHotkey(
                "email",
                KeyCode.VcE,
                ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                ProfileHotkeyBehavior.StartDictation
            )
        );
        var toggleCount = 0;
        d.ProfileDictationToggleRequested += _ => toggleCount++;

        d.Handle(KeyCode.VcE, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        d.Handle(KeyCode.VcE, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);

        Assert.Equal(1, toggleCount);
    }

    [Fact]
    public void ProfileProcessSelectedText_WaitsForReleaseAndDoesNotStartDictation()
    {
        var d = new ShortcutDispatcher();
        d.UpdateShortcuts(
            SetWithProfileHotkey(
                "summarize",
                KeyCode.VcS,
                ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                ProfileHotkeyBehavior.ProcessSelectedText
            )
        );
        string? observed = null;
        var textCount = 0;
        var dictationCount = 0;
        d.ProfileTextProcessingRequested += id =>
        {
            observed = id;
            textCount++;
        };
        d.ProfileDictationToggleRequested += _ => dictationCount++;
        d.ProfileDictationStartRequested += _ => dictationCount++;

        d.Handle(KeyCode.VcS, ModifierMask.LeftCtrl | ModifierMask.LeftShift, true);
        Assert.Null(observed);
        Assert.Equal(0, textCount);
        Assert.Equal(0, dictationCount);

        d.Handle(KeyCode.VcS, ModifierMask.LeftCtrl | ModifierMask.LeftShift, false);
        d.Handle(KeyCode.VcLeftControl, ModifierMask.LeftShift, false);
        Assert.Equal(0, textCount);

        d.Handle(KeyCode.VcLeftShift, ModifierMask.None, false);

        Assert.Equal("summarize", observed);
        Assert.Equal(1, textCount);
        Assert.Equal(0, dictationCount);
    }

    private static GlobalShortcutSet SetWithProfileHotkey(
        string profileId,
        KeyCode key,
        ModifierMask mods,
        ProfileHotkeyBehavior behavior,
        RecordingMode mode = RecordingMode.Toggle
    )
    {
        return new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            mode,
            false,
            [],
            [new ProfileHotkey(profileId, key, mods, behavior)]
        );
    }

    private static GlobalShortcutSet Set(RecordingMode mode, bool cancelEnabled = false)
    {
        return new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            KeyCode.VcP,
            ModifierMask.LeftCtrl,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            mode,
            cancelEnabled
        );
    }

    private static GlobalShortcutSet SetWithPromptAction(
        string actionId,
        KeyCode key,
        ModifierMask mods
    )
    {
        return new GlobalShortcutSet(
            KeyCode.VcSpace,
            ModifierMask.LeftCtrl | ModifierMask.LeftShift,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            null,
            ModifierMask.None,
            KeyCode.VcEscape,
            ModifierMask.None,
            RecordingMode.Toggle,
            false,
            [new PromptActionHotkey(actionId, key, mods)],
            []
        );
    }
}
