using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void TrySetHotkeyFromString_ParsesModifiersAndKeys()
    {
        var hotkey = new HotkeyService();

        var parsed = hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        Assert.True(parsed);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public void TrySetPromptPaletteHotkeyFromString_RejectsInvalidBinding()
    {
        var hotkey = new HotkeyService();
        hotkey.SetPromptPaletteHotkey(KeyCode.VcP, ModifierMask.LeftCtrl);

        var parsed = hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Nope");

        Assert.False(parsed);
        Assert.Equal("Ctrl+P", hotkey.CurrentPromptPaletteHotkeyString);
    }

    [Fact]
    public void ModifiersMatch_TreatsRightCtrlAsEquivalentToLeftCtrl()
    {
        var matches = HotkeyService.ModifiersMatch(ModifierMask.RightCtrl, ModifierMask.LeftCtrl);

        Assert.True(matches);
    }

    [Fact]
    public async Task Initialize_RecordsRequiresToggleModeFromBackend()
    {
        var backend = new TestShortcutBackend
        {
            NextResult = new GlobalShortcutRegistrationResult(
                true,
                "test",
                null,
                true,
                null
            )
        };
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));

        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        Assert.True(hotkey.BackendRequiresToggleMode);
        Assert.True(backend.RegisterCount >= 1);
    }

    [Fact]
    public async Task PushShortcuts_FailedRegistration_RaisesHookFailed()
    {
        var backend = new TestShortcutBackend
        {
            NextResult = new GlobalShortcutRegistrationResult(
                false,
                "test",
                "boom",
                false,
                null
            )
        };
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        string? observed = null;
        hotkey.HookFailed += (_, msg) => observed = msg;

        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        Assert.Equal("boom", observed);
    }

    [Fact]
    public async Task SwitchBackend_DisposesPreviousAndResolvesNew()
    {
        // Each call to selector.Resolve() must produce a fresh backend so
        // the dispose path can't be swallowed by sharing the same instance.
        var backendA = new TestShortcutBackend();
        var backendB = new TestShortcutBackend();
        var queue = new Queue<IGlobalShortcutBackend>(
            new IGlobalShortcutBackend[] { backendA, backendB }
        );
        var selector = new BackendSelector(() => queue.Dequeue());
        using var hotkey = new HotkeyService(selector);
        hotkey.Initialize();
        await backendA.WaitUntilSettledAsync();
        Assert.Equal("test", hotkey.ActiveBackendId);
        Assert.False(backendA.Disposed);

        await hotkey.SwitchBackendAsync();
        await backendB.WaitUntilSettledAsync();

        Assert.True(backendA.Disposed);
        Assert.False(backendB.Disposed);
        Assert.True(backendB.RegisterCount >= 1);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_RaisesPromptActionHotkeyTriggeredWithActionId()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("alpha", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        await backend.WaitUntilSettledAsync();

        string? observed = null;
        hotkey.PromptActionHotkeyTriggered += (_, id) => observed = id;

        backend.RaisePromptAction("alpha");

        Assert.Equal("alpha", observed);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_DropsEntryCollidingWithDictation()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Default dictation hotkey is Ctrl+Shift+Space — the collision check
        // must reject a prompt-action entry that names the same chord.
        hotkey.SetPromptActionHotkeys(
            [
                new PromptActionHotkey(
                    "collides",
                    KeyCode.VcSpace,
                    ModifierMask.LeftCtrl | ModifierMask.LeftShift
                ),
                new PromptActionHotkey(
                    "keeper",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                )
            ]
        );

        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.PromptActionHotkeys);
        Assert.Equal("keeper", kept.ActionId);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_KeepsFirstDuplicateAndDropsSecond()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        hotkey.SetPromptActionHotkeys(
            [
                new PromptActionHotkey(
                    "first",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                ),
                new PromptActionHotkey(
                    "second",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                )
            ]
        );

        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.PromptActionHotkeys);
        Assert.Equal("first", kept.ActionId);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_DropsIntraBatchPrefixCollision()
    {
        // Adversarial-review follow-up: the intra-batch collision check
        // must also honor the side-specific modifier prefix-collision rule.
        // A batch containing both `Left Ctrl` (modifier-only) and a Ctrl
        // chord would otherwise accept both and shadow the chord at
        // runtime — the fixed-bindings collision check at GetBoundHotkeys()
        // can't see in-flight prompt-action entries during the reconcile.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Reset dictation to a non-Ctrl chord so the fixed-bindings check
        // doesn't pre-reject "Left Ctrl" before the intra-batch check runs.
        Assert.True(hotkey.TrySetHotkeyFromString("Shift+F9"));
        await backend.WaitUntilSettledAsync();

        hotkey.SetPromptActionHotkeys(
            [
                new PromptActionHotkey("modifier-only", KeyCode.VcLeftControl, ModifierMask.None),
                new PromptActionHotkey(
                    "ctrl-chord",
                    KeyCode.VcF12,
                    ModifierMask.LeftCtrl
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.PromptActionHotkeys);
        Assert.Equal("modifier-only", kept.ActionId);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_SecondCallReplacesPreviousList()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("old", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        await backend.WaitUntilSettledAsync();

        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("new", KeyCode.VcT, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var entry = Assert.Single(snapshot!.PromptActionHotkeys);
        Assert.Equal("new", entry.ActionId);
        Assert.Equal(KeyCode.VcT, entry.Key);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_RejectsChordAlreadyBoundToPromptAction()
    {
        // Symmetry guard: SetPromptActionHotkeys already refuses entries
        // colliding with fixed bindings; the inverse must hold too, or the
        // matcher's prompt-action arm silently shadows the fixed binding.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        hotkey.SetPromptActionHotkeys(
            [
                new PromptActionHotkey(
                    "alpha",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetHotkeyFromString("Ctrl+Alt+R");

        Assert.False(accepted);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_AcceptsUnchangedListWithoutSelfConflict()
    {
        // Regression: ActionsChanged fires on every add/update/delete, so
        // most calls re-submit the same list. The reconcile must not treat
        // existing entries as colliding with themselves.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        var entries = new[]
        {
            new PromptActionHotkey("alpha", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt),
            new PromptActionHotkey("beta", KeyCode.VcT, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)
        };

        hotkey.SetPromptActionHotkeys(entries);
        await backend.WaitUntilSettledAsync();
        hotkey.SetPromptActionHotkeys(entries);
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.PromptActionHotkeys.Count);
    }

    [Fact]
    public void ParsePromptActionHotkeys_SkipsDisabledOrUnparseableActions()
    {
        var parsed = HotkeyService.ParsePromptActionHotkeys(
            [
                new PromptAction
                {
                    Id = "enabled",
                    Name = "E",
                    SystemPrompt = "x",
                    HotkeyKey = "Ctrl+Alt+R"
                },
                new PromptAction
                {
                    Id = "disabled",
                    Name = "D",
                    SystemPrompt = "x",
                    IsEnabled = false,
                    HotkeyKey = "Ctrl+Alt+T"
                },
                new PromptAction
                {
                    Id = "no-hotkey",
                    Name = "N",
                    SystemPrompt = "x"
                },
                new PromptAction
                {
                    Id = "bad",
                    Name = "B",
                    SystemPrompt = "x",
                    HotkeyKey = "Not+a+real+combo"
                }
            ]
        );

        var entry = Assert.Single(parsed);
        Assert.Equal("enabled", entry.ActionId);
        Assert.Equal(KeyCode.VcR, entry.Key);
    }

    [Fact]
    public async Task SetProfileHotkeys_RaisesProfileDictationToggleRequestedWithId()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "email",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        string? observed = null;
        hotkey.ProfileDictationToggleRequested += (_, id) => observed = id;

        backend.RaiseProfileDictationToggle("email");

        Assert.Equal("email", observed);
    }

    [Fact]
    public async Task SetProfileHotkeys_RaisesProfileTextProcessingRequestedWithId()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "summarize",
                    KeyCode.VcS,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.ProcessSelectedText
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        string? observed = null;
        hotkey.ProfileTextProcessingRequested += (_, id) => observed = id;

        backend.RaiseProfileTextProcessing("summarize");

        Assert.Equal("summarize", observed);
    }

    [Fact]
    public async Task SetProfileHotkeys_DropsEntryCollidingWithDictation()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Default dictation hotkey is Ctrl+Shift+Space.
        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "collides",
                    KeyCode.VcSpace,
                    ModifierMask.LeftCtrl | ModifierMask.LeftShift,
                    ProfileHotkeyBehavior.StartDictation
                ),
                new ProfileHotkey(
                    "keeper",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.ProfileHotkeys);
        Assert.Equal("keeper", kept.ProfileId);
    }

    [Fact]
    public async Task SetProfileHotkeys_DropsEntryCollidingWithPromptAction()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("action", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        await backend.WaitUntilSettledAsync();

        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "collides",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                ),
                new ProfileHotkey(
                    "keeper",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.ProfileHotkeys);
        Assert.Equal("keeper", kept.ProfileId);
    }

    [Fact]
    public async Task SetProfileHotkeys_KeepsFirstDuplicateAndDropsSecond()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "first",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                ),
                new ProfileHotkey(
                    "second",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.ProcessSelectedText
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.ProfileHotkeys);
        Assert.Equal("first", kept.ProfileId);
    }

    [Fact]
    public void ParseProfileHotkeys_SkipsDisabledBlankUnparseable_AndCarriesBehavior()
    {
        var parsed = HotkeyService.ParseProfileHotkeys(
            [
                new Profile
                {
                    Id = "dictate",
                    Name = "Dictate",
                    HotkeyData = "Ctrl+Alt+E",
                    HotkeyBehavior = ProfileHotkeyBehavior.StartDictation
                },
                new Profile
                {
                    Id = "selection",
                    Name = "Selection",
                    HotkeyData = "Ctrl+Alt+S",
                    HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText
                },
                new Profile
                {
                    Id = "disabled",
                    Name = "Disabled",
                    IsEnabled = false,
                    HotkeyData = "Ctrl+Alt+T"
                },
                new Profile
                {
                    Id = "no-hotkey",
                    Name = "None"
                },
                new Profile
                {
                    Id = "bad",
                    Name = "Bad",
                    HotkeyData = "Not+a+real+combo"
                }
            ]
        );

        Assert.Equal(2, parsed.Count);
        var dictate = parsed.Single(p => p.ProfileId == "dictate");
        Assert.Equal(KeyCode.VcE, dictate.Key);
        Assert.Equal(ProfileHotkeyBehavior.StartDictation, dictate.Behavior);
        var selection = parsed.Single(p => p.ProfileId == "selection");
        Assert.Equal(ProfileHotkeyBehavior.ProcessSelectedText, selection.Behavior);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_RejectsChordAlreadyBoundToProfile()
    {
        // Symmetry guard mirroring the prompt-action case: a fixed-binding
        // change must be refused if it would shadow an existing profile chord.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "email",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetHotkeyFromString("Ctrl+Alt+R");

        Assert.False(accepted);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public async Task PushShortcuts_AppliesUpdatesInOrder()
    {
        // Backend records each set it sees. A burst of TrySet* calls must
        // arrive at the backend in the same order they were issued so the
        // last-write-wins state stays consistent with CurrentHotkeyString.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        Assert.True(hotkey.TrySetHotkeyFromString("Ctrl+Alt+F1"));
        Assert.True(hotkey.TrySetHotkeyFromString("Ctrl+Alt+F2"));
        Assert.True(hotkey.TrySetHotkeyFromString("Ctrl+Alt+F3"));

        await backend.WaitUntilSettledAsync();

        var lastSeen = backend.LastSet;
        Assert.NotNull(lastSeen);
        Assert.Equal(KeyCode.VcF3, lastSeen!.DictationKey);
    }

    [Theory]
    [InlineData("Left Ctrl", KeyCode.VcLeftControl)]
    [InlineData("Right Ctrl", KeyCode.VcRightControl)]
    [InlineData("Left Control", KeyCode.VcLeftControl)]
    [InlineData("Right Control", KeyCode.VcRightControl)]
    [InlineData("Left Shift", KeyCode.VcLeftShift)]
    [InlineData("Right Shift", KeyCode.VcRightShift)]
    [InlineData("Left Alt", KeyCode.VcLeftAlt)]
    [InlineData("Right Alt", KeyCode.VcRightAlt)]
    [InlineData("Left Meta", KeyCode.VcLeftMeta)]
    [InlineData("Right Meta", KeyCode.VcRightMeta)]
    [InlineData("Left Super", KeyCode.VcLeftMeta)]
    [InlineData("Right Win", KeyCode.VcRightMeta)]
    [InlineData("right alt", KeyCode.VcRightAlt)]
    [InlineData("RIGHT ALT", KeyCode.VcRightAlt)]
    [InlineData("  Right Alt  ", KeyCode.VcRightAlt)]
    public async Task TrySetHotkeyFromString_SideSpecificSingleModifier_ParsesAndPushesEmptyMask(
        string input,
        KeyCode expectedKey
    )
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        var parsed = hotkey.TrySetHotkeyFromString(input);
        await backend.WaitUntilSettledAsync();

        Assert.True(parsed);
        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(expectedKey, snapshot!.DictationKey);
        Assert.Equal(ModifierMask.None, snapshot.DictationModifiers);
    }

    [Theory]
    [InlineData("Left Ctrl")]
    [InlineData("Right Alt")]
    [InlineData("Right Meta")]
    public void TrySetHotkeyFromString_SideSpecificSingleModifier_RoundTripsThroughCurrentHotkeyString(
        string input
    )
    {
        var hotkey = new HotkeyService();

        Assert.True(hotkey.TrySetHotkeyFromString(input));
        Assert.Equal(input, hotkey.CurrentHotkeyString);
    }

    [Fact]
    public void TrySetHotkeyFromString_SideSpecificChord_FallsThroughAndIsRejected()
    {
        // Pins the Tier-A/Tier-B boundary: "Right Alt+R" must NOT take the
        // single-modifier early-return path (which would silently absorb the
        // side prefix). It falls through to the chord loop, which can't
        // resolve "right alt" as a single token and rejects the input.
        var hotkey = new HotkeyService();
        hotkey.SetHotkey(KeyCode.VcSpace, ModifierMask.LeftCtrl | ModifierMask.LeftShift);

        var parsed = hotkey.TrySetHotkeyFromString("Right Alt+R");

        Assert.False(parsed);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public void FormatHotkey_SideSpecificModifierWithExtraMask_FallsBackToDefaultFormat()
    {
        // Round-trip stability: the side-specific shorthand "Right Alt" is
        // emitted ONLY when the binding has no other modifier flags. A
        // (VcRightAlt, Shift) chord must format unambiguously (not "Right
        // Alt", which would round-trip to (VcRightAlt, None)).
        var hotkey = new HotkeyService();
        hotkey.SetHotkey(KeyCode.VcRightAlt, ModifierMask.LeftShift);

        Assert.Equal("Shift+RightAlt", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_SideSpecificModifiers_LeftAndRightDoNotCollide()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        Assert.True(hotkey.TrySetHotkeyFromString("Right Alt"));
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Left Alt"));
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(KeyCode.VcRightAlt, snapshot!.DictationKey);
        Assert.Equal(KeyCode.VcLeftAlt, snapshot.PromptPaletteKey);
    }

    [Fact]
    public async Task TrySetPromptPaletteHotkeyFromString_SideSpecificModifierAlreadyBound_IsRejected()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        Assert.True(hotkey.TrySetHotkeyFromString("Right Alt"));
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetPromptPaletteHotkeyFromString("Right Alt");

        Assert.False(accepted);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_SideSpecificModifierPrefixesExistingChord_IsRejected()
    {
        // Adversarial-review regression: a modifier-only binding fires on
        // the bare modifier press, which is also the first keystroke of
        // any chord using that physical modifier. Without prefix-collision
        // detection, "Left Ctrl" as one action + "Ctrl+Shift+Space" as
        // dictation would both fire when the user presses Left Ctrl +
        // Shift + Space, silently shadowing the chord.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Dictation default is Ctrl+Shift+Space.
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetPromptPaletteHotkeyFromString("Left Ctrl");

        Assert.False(accepted);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_ChordUsingModifierAlreadyBoundAsModifierOnly_IsRejected()
    {
        // Inverse direction of the prefix-collision rule: once a modifier
        // is bound as a single-modifier action, a chord that uses that
        // same physical modifier must also be rejected — symmetric, or
        // the user could create the collision in either order.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Right Alt"));
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetHotkeyFromString("Alt+F9");

        Assert.False(accepted);
        // Original default dictation hotkey is preserved.
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_SideSpecificModifierAndChordUsingDifferentModifier_AreAllowed()
    {
        // The prefix-collision rule must be scoped to the SAME physical
        // modifier group. Binding "Left Ctrl" must not block an unrelated
        // chord like "Alt+F9" — only Ctrl chords collide. Reset dictation
        // to a non-Ctrl chord first so the default Ctrl+Shift+Space doesn't
        // itself shadow the modifier-only binding.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        Assert.True(hotkey.TrySetHotkeyFromString("Shift+F9"));
        await backend.WaitUntilSettledAsync();

        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Left Ctrl"));
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(KeyCode.VcF9, snapshot!.DictationKey);
        Assert.Equal(ModifierMask.LeftShift, snapshot.DictationModifiers);
        Assert.Equal(KeyCode.VcLeftControl, snapshot.PromptPaletteKey);
    }

    [Fact]
    public async Task TrySetHotkeyFromString_LeftCtrlModifierOnly_CollidesWithRightCtrlChord()
    {
        // Cross-side regression: because ShortcutMatcher.ModifiersMatch
        // collapses Left/Right, a chord stored with RightCtrl can still be
        // satisfied by a LeftCtrl press. The modifier-only binding's
        // physical group must cover both sides so the chord is rejected
        // regardless of which side flag the chord was parsed with.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Construct a chord that stores RightCtrl explicitly (the public
        // parser always normalizes to LeftCtrl, so use SetHotkey directly).
        hotkey.SetHotkey(KeyCode.VcSpace, ModifierMask.RightCtrl | ModifierMask.RightShift);
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetPromptPaletteHotkeyFromString("Left Ctrl");

        Assert.False(accepted);
    }

    [Fact]
    public async Task SetPromptActionHotkeys_DropsEntryThatPrefixesExistingChord()
    {
        // Prompt-action hotkeys share the same collision check via
        // HotkeyMatchesAny, so the prefix rule must apply there too —
        // otherwise the B12 dynamic binding list would be the back door.
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        // Default dictation is Ctrl+Shift+Space, so any Ctrl-prefix
        // modifier-only entry should be dropped from the prompt-action list.
        hotkey.SetPromptActionHotkeys(
            [
                new PromptActionHotkey("collides", KeyCode.VcLeftControl, ModifierMask.None),
                new PromptActionHotkey(
                    "keeper",
                    KeyCode.VcR,
                    ModifierMask.LeftAlt | ModifierMask.LeftMeta
                )
            ]
        );

        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        var kept = Assert.Single(snapshot!.PromptActionHotkeys);
        Assert.Equal("keeper", kept.ActionId);
    }

    private sealed class TestShortcutBackend : IGlobalShortcutBackend
    {
        private readonly TaskCompletionSource _gate = new();
        private int _pending;

        public GlobalShortcutRegistrationResult NextResult { get; set; } =
            new(
                true,
                "test",
                null,
                false,
                null
            );

        public int RegisterCount { get; private set; }
        public GlobalShortcutSet? LastSet { get; private set; }
        public bool Disposed { get; private set; }

        public string Id => "test";
        public string DisplayName => "Test";
        public bool SupportsPressRelease => true;
        public bool IsGlobalScope => true;

        public bool IsAvailable()
        {
            return true;
        }

        public event EventHandler? DictationToggleRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? DictationStartRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? DictationStopRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? PromptPaletteRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? TransformSelectionRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? RecentTranscriptionsRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? CopyLastTranscriptionRequested
        {
            add { }
            remove { }
        }

        public event EventHandler? CancelRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? PromptActionRequested;

        public event EventHandler<string>? ProfileDictationToggleRequested;
        public event EventHandler<string>? ProfileDictationStartRequested;

        public event EventHandler? ProfileDictationStopRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? ProfileTextProcessingRequested;

        public event EventHandler<string>? Failed
        {
            add { }
            remove { }
        }

        public void RaisePromptAction(string actionId)
        {
            PromptActionRequested?.Invoke(this, actionId);
        }

        public void RaiseProfileDictationToggle(string profileId)
        {
            ProfileDictationToggleRequested?.Invoke(this, profileId);
        }

        public void RaiseProfileDictationStart(string profileId)
        {
            ProfileDictationStartRequested?.Invoke(this, profileId);
        }

        public void RaiseProfileTextProcessing(string profileId)
        {
            ProfileTextProcessingRequested?.Invoke(this, profileId);
        }

        public Task<GlobalShortcutRegistrationResult> RegisterAsync(
            GlobalShortcutSet shortcuts,
            CancellationToken ct
        )
        {
            _gate.TrySetResult();
            Interlocked.Increment(ref _pending);
            RegisterCount++;
            LastSet = shortcuts;
            Interlocked.Decrement(ref _pending);
            return Task.FromResult(NextResult);
        }

        public Task UnregisterAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public async Task WaitUntilSettledAsync()
        {
            // Spin briefly to let the coordinator's chained continuations
            // drain — they run on the thread-pool scheduler so a yield is
            // enough in normal cases; a short timeout guards against hangs.
            await Task.WhenAny(_gate.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _pending) == 0)
                {
                    await Task.Delay(20);
                    if (Volatile.Read(ref _pending) == 0)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }
        }
    }
}