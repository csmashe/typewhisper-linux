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
        using var hotkey = TestShortcutBackend.CreateHotkeyService();

        var parsed = hotkey.TrySetHotkeyFromString("Ctrl+Shift+Space");

        Assert.True(parsed);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
    }

    [Fact]
    public void TrySetPromptPaletteHotkeyFromString_RejectsInvalidBinding()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
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
    public void ValidatePromptActionHotkeyCandidate_ReportsMalformedNonblankChord()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();

        var result = hotkey.ValidatePromptActionHotkeyCandidate(
            "Ctrl+DefinitelyNotAKey",
            null,
            [],
            []
        );

        Assert.Equal(HotkeyCandidateValidationStatus.Malformed, result.Status);
        Assert.False(result.IsValid);
        Assert.Null(result.NormalizedHotkey);
    }

    [Fact]
    public void ValidatePromptActionHotkeyCandidate_RejectsFixedShortcutCollision()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();

        var result = hotkey.ValidatePromptActionHotkeyCandidate(
            "Control+Shift+Space",
            null,
            [],
            []
        );

        Assert.Equal(
            HotkeyCandidateValidationStatus.CollidesWithFixedBinding,
            result.Status
        );
    }

    [Fact]
    public void ValidatePromptActionHotkeyCandidate_RejectsEnabledPromptActionCollision()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var actions = new[]
        {
            new PromptAction
            {
                Id = "other",
                Name = "Other",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8"
            }
        };

        var result = hotkey.ValidatePromptActionHotkeyCandidate(
            " alt + f8 ",
            "edited",
            actions,
            []
        );

        Assert.Equal(
            HotkeyCandidateValidationStatus.CollidesWithPromptAction,
            result.Status
        );
    }

    [Fact]
    public void ValidatePromptActionHotkeyCandidate_UsesCrossSideModifierPrefixForProfiles()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var profiles = new[]
        {
            new Profile
            {
                Id = "other",
                Name = "Other",
                HotkeyData = "Right Ctrl"
            }
        };

        var result = hotkey.ValidatePromptActionHotkeyCandidate(
            "Ctrl+Alt+R",
            "edited",
            [],
            profiles
        );

        Assert.Equal(
            HotkeyCandidateValidationStatus.CollidesWithProfile,
            result.Status
        );
    }

    [Fact]
    public void ValidateCandidates_AllowOwnUnchangedBindingAndIgnoreDisabledOthers()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var actions = new[]
        {
            new PromptAction
            {
                Id = "edited-action",
                Name = "Edited",
                SystemPrompt = "x",
                HotkeyKey = "alt+f8"
            },
            new PromptAction
            {
                Id = "disabled-action",
                Name = "Disabled",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
                IsEnabled = false
            }
        };
        var profiles = new[]
        {
            new Profile
            {
                Id = "edited-profile",
                Name = "Edited",
                HotkeyData = "Meta+F9"
            },
            new Profile
            {
                Id = "disabled-profile",
                Name = "Disabled",
                HotkeyData = "Meta+F9",
                IsEnabled = false
            }
        };

        var actionResult = hotkey.ValidatePromptActionHotkeyCandidate(
            " ALT + f8 ",
            "edited-action",
            actions,
            []
        );
        var profileResult = hotkey.ValidateProfileHotkeyCandidate(
            " super + f9 ",
            ProfileHotkeyBehavior.StartDictation,
            null,
            "edited-profile",
            [],
            profiles
        );

        Assert.True(actionResult.IsValid);
        Assert.Equal("Alt+F8", actionResult.NormalizedHotkey);
        Assert.True(profileResult.IsValid);
        Assert.Equal("Meta+F9", profileResult.NormalizedHotkey);
    }

    [Fact]
    public async Task ValidatePromptActionHotkeyCandidate_DoesNotRegisterOrMutateBindings()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var registerCount = backend.RegisterCount;
        var originalHotkey = hotkey.CurrentHotkeyString;

        var result = hotkey.ValidatePromptActionHotkeyCandidate(
            "Alt+F8",
            null,
            [],
            []
        );
        await backend.WaitUntilSettledAsync();

        Assert.True(result.IsValid);
        Assert.Equal(registerCount, backend.RegisterCount);
        Assert.Equal(originalHotkey, hotkey.CurrentHotkeyString);
        Assert.Empty(backend.LastSet?.PromptActionHotkeys ?? []);
        Assert.Empty(backend.LastSet?.ProfileHotkeys ?? []);
    }

    [Fact]
    public void ValidateProfileHotkeyCandidate_RequiresUsableSelectedTextDestination()
    {
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
        var disabled = new PromptAction
        {
            Id = "disabled",
            Name = "Disabled",
            SystemPrompt = "x",
            IsEnabled = false
        };
        var enabled = disabled with { Id = "enabled", Name = "Enabled", IsEnabled = true };

        var nullResult = hotkey.ValidateProfileHotkeyCandidate(
            "Meta+F9",
            ProfileHotkeyBehavior.ProcessSelectedText,
            null,
            "profile",
            [disabled, enabled],
            []
        );
        var missingResult = hotkey.ValidateProfileHotkeyCandidate(
            "Meta+F9",
            ProfileHotkeyBehavior.ProcessSelectedText,
            "missing",
            "profile",
            [disabled, enabled],
            []
        );
        var disabledResult = hotkey.ValidateProfileHotkeyCandidate(
            "Meta+F9",
            ProfileHotkeyBehavior.ProcessSelectedText,
            "disabled",
            "profile",
            [disabled, enabled],
            []
        );
        var enabledResult = hotkey.ValidateProfileHotkeyCandidate(
            " super + f9 ",
            ProfileHotkeyBehavior.ProcessSelectedText,
            "enabled",
            "profile",
            [disabled, enabled],
            []
        );
        var blankResult = hotkey.ValidateProfileHotkeyCandidate(
            "   ",
            ProfileHotkeyBehavior.ProcessSelectedText,
            null,
            "profile",
            [],
            []
        );

        Assert.Equal(
            HotkeyCandidateValidationStatus.MissingEnabledPromptAction,
            nullResult.Status
        );
        Assert.Equal(
            HotkeyCandidateValidationStatus.MissingEnabledPromptAction,
            missingResult.Status
        );
        Assert.Equal(
            HotkeyCandidateValidationStatus.MissingEnabledPromptAction,
            disabledResult.Status
        );
        Assert.True(enabledResult.IsValid);
        Assert.Equal("Meta+F9", enabledResult.NormalizedHotkey);
        Assert.True(blankResult.IsValid);
        Assert.Null(blankResult.NormalizedHotkey);
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
            [backendA, backendB]
        );
        var selector = new BackendSelector(queue.Dequeue);
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
        var kept = Assert.Single(snapshot.PromptActionHotkeys);
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
        var kept = Assert.Single(snapshot.PromptActionHotkeys);
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
        var kept = Assert.Single(snapshot.PromptActionHotkeys);
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
        var entry = Assert.Single(snapshot.PromptActionHotkeys);
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
        Assert.Equal(2, snapshot.PromptActionHotkeys.Count);
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
        var kept = Assert.Single(snapshot.ProfileHotkeys);
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
        var kept = Assert.Single(snapshot.ProfileHotkeys);
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
        var kept = Assert.Single(snapshot.ProfileHotkeys);
        Assert.Equal("first", kept.ProfileId);
    }

    [Fact]
    public async Task DynamicHotkeys_CrossListWinnerIsIndependentOfSetterOrder()
    {
        var action = new PromptActionHotkey(
            "action",
            KeyCode.VcR,
            ModifierMask.LeftCtrl | ModifierMask.LeftAlt
        );
        var profile = new ProfileHotkey(
            "profile",
            KeyCode.VcR,
            ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
            ProfileHotkeyBehavior.StartDictation
        );
        var actionFirstBackend = new TestShortcutBackend();
        var profileFirstBackend = new TestShortcutBackend();
        using var actionFirst = new HotkeyService(
            new BackendSelector(() => actionFirstBackend)
        );
        using var profileFirst = new HotkeyService(
            new BackendSelector(() => profileFirstBackend)
        );
        actionFirst.Initialize();
        profileFirst.Initialize();

        actionFirst.SetPromptActionHotkeys([action]);
        actionFirst.SetProfileHotkeys([profile]);
        profileFirst.SetProfileHotkeys([profile]);
        profileFirst.SetPromptActionHotkeys([action]);
        await Task.WhenAll(
            actionFirstBackend.WaitUntilSettledAsync(),
            profileFirstBackend.WaitUntilSettledAsync()
        );

        foreach (var snapshot in new[]
                 {
                     actionFirstBackend.LastSet,
                     profileFirstBackend.LastSet
                 })
        {
            Assert.NotNull(snapshot);
            Assert.Equal(
                ["action"],
                snapshot.PromptActionHotkeys.Select(entry => entry.ActionId)
            );
            Assert.Empty(snapshot.ProfileHotkeys);
        }
    }

    [Fact]
    public async Task DynamicHotkeys_RemovingWinnerResurrectsRetainedProfileCandidate()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("action", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        hotkey.SetProfileHotkeys(
            [
                new ProfileHotkey(
                    "profile",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        hotkey.SetPromptActionHotkeys([]);
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.PromptActionHotkeys);
        var resurrected = Assert.Single(snapshot.ProfileHotkeys);
        Assert.Equal("profile", resurrected.ProfileId);
    }

    [Fact]
    public async Task DynamicHotkeys_IncrementalResultMatchesFreshCombinedReconciliation()
    {
        PromptActionHotkey[] actions =
        [
            new("action-winner", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt),
            new("action-only", KeyCode.VcT, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)
        ];
        ProfileHotkey[] profiles =
        [
            new(
                "profile-loser",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                ProfileHotkeyBehavior.StartDictation
            ),
            new(
                "profile-only",
                KeyCode.VcE,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                ProfileHotkeyBehavior.ProcessSelectedText
            )
        ];
        var incrementalBackend = new TestShortcutBackend();
        var freshBackend = new TestShortcutBackend();
        using var incremental = new HotkeyService(
            new BackendSelector(() => incrementalBackend)
        );
        using var fresh = new HotkeyService(new BackendSelector(() => freshBackend));
        incremental.Initialize();
        fresh.Initialize();

        incremental.SetProfileHotkeys(profiles);
        incremental.SetPromptActionHotkeys(actions);
        fresh.SetDynamicHotkeys(actions, profiles);
        await Task.WhenAll(
            incrementalBackend.WaitUntilSettledAsync(),
            freshBackend.WaitUntilSettledAsync()
        );

        var incrementalSnapshot = incrementalBackend.LastSet;
        var freshSnapshot = freshBackend.LastSet;
        Assert.NotNull(incrementalSnapshot);
        Assert.NotNull(freshSnapshot);
        Assert.Equal(
            freshSnapshot.PromptActionHotkeys.Select(entry => entry.ActionId),
            incrementalSnapshot.PromptActionHotkeys.Select(entry => entry.ActionId)
        );
        Assert.Equal(
            freshSnapshot.ProfileHotkeys.Select(entry => entry.ProfileId),
            incrementalSnapshot.ProfileHotkeys.Select(entry => entry.ProfileId)
        );
    }

    [Fact]
    public async Task SetDynamicHotkeys_ReturnsIdentifyingMessageForEveryRejection()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();

        var rejections = hotkey.SetDynamicHotkeys(
            [
                new PromptActionHotkey(
                    "action-winner",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                ),
                new PromptActionHotkey(
                    "duplicate-action",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                ),
                new PromptActionHotkey(
                    "fixed-action",
                    KeyCode.VcSpace,
                    ModifierMask.LeftCtrl | ModifierMask.LeftShift
                ),
                new PromptActionHotkey(
                    "",
                    KeyCode.VcT,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt
                )
            ],
            [
                new ProfileHotkey(
                    "colliding-profile",
                    KeyCode.VcR,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.StartDictation
                ),
                new ProfileHotkey(
                    "profile-winner",
                    KeyCode.VcE,
                    ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                    ProfileHotkeyBehavior.ProcessSelectedText
                )
            ]
        );
        await backend.WaitUntilSettledAsync();

        Assert.Equal(4, rejections.Count);
        Assert.Equal(rejections.Count, rejections.Distinct().Count());
        Assert.Contains(
            rejections,
            message =>
                message.Contains("duplicate-action", StringComparison.Ordinal)
                && message.Contains("Ctrl+Alt+R", StringComparison.Ordinal)
                && message.Contains("higher-priority", StringComparison.Ordinal)
        );
        Assert.Contains(
            rejections,
            message =>
                message.Contains("colliding-profile", StringComparison.Ordinal)
                && message.Contains("Ctrl+Alt+R", StringComparison.Ordinal)
                && message.Contains("higher-priority", StringComparison.Ordinal)
        );
        Assert.Contains(
            rejections,
            message =>
                message.Contains("fixed-action", StringComparison.Ordinal)
                && message.Contains("Ctrl+Shift+Space", StringComparison.Ordinal)
                && message.Contains("higher-priority", StringComparison.Ordinal)
        );
        Assert.Contains(
            rejections,
            message =>
                message.Contains("Prompt-action", StringComparison.Ordinal)
                && message.Contains("Ctrl+Alt+T", StringComparison.Ordinal)
                && message.Contains("blank", StringComparison.Ordinal)
        );

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(
            ["action-winner"],
            snapshot.PromptActionHotkeys.Select(entry => entry.ActionId)
        );
        Assert.Equal(
            ["profile-winner"],
            snapshot.ProfileHotkeys.Select(entry => entry.ProfileId)
        );
    }

    [Fact]
    public async Task DynamicHotkeys_DefensivelySnapshotsRetainedCandidates()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        var profiles = new List<ProfileHotkey>
        {
            new(
                "profile",
                KeyCode.VcR,
                ModifierMask.LeftCtrl | ModifierMask.LeftAlt,
                ProfileHotkeyBehavior.StartDictation
            )
        };
        hotkey.SetProfileHotkeys(profiles);

        profiles.Clear();
        hotkey.SetPromptActionHotkeys(
            [new PromptActionHotkey("action", KeyCode.VcR, ModifierMask.LeftCtrl | ModifierMask.LeftAlt)]
        );
        hotkey.SetPromptActionHotkeys([]);
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(
            ["profile"],
            snapshot.ProfileHotkeys.Select(entry => entry.ProfileId)
        );
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
        Assert.Equal(KeyCode.VcF3, lastSeen.DictationKey);
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
        Assert.Equal(expectedKey, snapshot.DictationKey);
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
        using var hotkey = TestShortcutBackend.CreateHotkeyService();

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
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
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
        using var hotkey = TestShortcutBackend.CreateHotkeyService();
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
        Assert.Equal(KeyCode.VcRightAlt, snapshot.DictationKey);
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
        Assert.Equal(KeyCode.VcF9, snapshot.DictationKey);
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
        var kept = Assert.Single(snapshot.PromptActionHotkeys);
        Assert.Equal("keeper", kept.ActionId);
    }

    [Fact]
    public async Task NativeDictationActive_SuppressesOnlyDictationAndPreservesEveryOtherRoute()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        Assert.True(hotkey.TrySetHotkeyFromString("Ctrl+Shift+F9"));
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Alt+P"));
        Assert.True(hotkey.TrySetRecentTranscriptionsHotkeyFromString("Ctrl+Alt+R"));
        Assert.True(hotkey.TrySetCopyLastTranscriptionHotkeyFromString("Ctrl+Alt+C"));
        Assert.True(hotkey.TrySetTransformSelectionHotkeyFromString("Ctrl+Alt+T"));
        hotkey.SetDynamicHotkeys(
            [new PromptActionHotkey("action", KeyCode.VcF10, ModifierMask.LeftMeta)],
            [
                new ProfileHotkey(
                    "profile",
                    KeyCode.VcF11,
                    ModifierMask.LeftMeta,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        hotkey.IsCancelShortcutEnabled = true;
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var configured = backend.LastSet;
        Assert.NotNull(configured);

        hotkey.SetNativeDictationBindingActive(true);
        await backend.WaitUntilSettledAsync();

        var suppressed = backend.LastSet;
        var registerCount = backend.RegisterCount;
        Assert.NotNull(suppressed);
        Assert.True(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcUndefined, suppressed.DictationKey);
        Assert.Equal(ModifierMask.None, suppressed.DictationModifiers);
        Assert.Equal(configured.PromptPaletteKey, suppressed.PromptPaletteKey);
        Assert.Equal(configured.PromptPaletteModifiers, suppressed.PromptPaletteModifiers);
        Assert.Equal(configured.RecentTranscriptionsKey, suppressed.RecentTranscriptionsKey);
        Assert.Equal(
            configured.RecentTranscriptionsModifiers,
            suppressed.RecentTranscriptionsModifiers
        );
        Assert.Equal(
            configured.CopyLastTranscriptionKey,
            suppressed.CopyLastTranscriptionKey
        );
        Assert.Equal(
            configured.CopyLastTranscriptionModifiers,
            suppressed.CopyLastTranscriptionModifiers
        );
        Assert.Equal(configured.TransformSelectionKey, suppressed.TransformSelectionKey);
        Assert.Equal(
            configured.TransformSelectionModifiers,
            suppressed.TransformSelectionModifiers
        );
        Assert.Equal(configured.CancelKey, suppressed.CancelKey);
        Assert.Equal(configured.CancelModifiers, suppressed.CancelModifiers);
        Assert.Equal(configured.Mode, suppressed.Mode);
        Assert.Equal(configured.IsCancelEnabled, suppressed.IsCancelEnabled);
        Assert.Equal(configured.PromptActionHotkeys.ToArray(), suppressed.PromptActionHotkeys);
        Assert.Equal(configured.ProfileHotkeys.ToArray(), suppressed.ProfileHotkeys);
        Assert.Equal("Ctrl+Shift+F9", hotkey.CurrentHotkeyString);

        hotkey.SetNativeDictationBindingActive(true);
        await backend.WaitUntilSettledAsync();
        Assert.Equal(registerCount, backend.RegisterCount);
    }

    [Theory]
    [InlineData(RecordingMode.PushToTalk, KeyCode.VcUndefined, false)]
    [InlineData(RecordingMode.Toggle, KeyCode.VcEscape, true)]
    public async Task NativeDictationActive_ProjectsCancelOnlyForPushToTalk(
        RecordingMode mode,
        KeyCode expectedCancelKey,
        bool expectedCancelEnabled
    )
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Mode = mode;
        hotkey.IsCancelShortcutEnabled = true;
        hotkey.Initialize();

        hotkey.SetNativeDictationBindingActive(true);
        await backend.WaitUntilSettledAsync();

        var snapshot = backend.LastSet;
        Assert.NotNull(snapshot);
        Assert.Equal(expectedCancelKey, snapshot.CancelKey);
        Assert.Equal(ModifierMask.None, snapshot.CancelModifiers);
        Assert.Equal(expectedCancelEnabled, snapshot.IsCancelEnabled);
        Assert.True(hotkey.IsCancelShortcutEnabled);
    }

    [Fact]
    public async Task NativeDictationInactive_RestoresConfiguredDictationAndCancelWithoutChangingOthers()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Mode = RecordingMode.PushToTalk;
        hotkey.IsCancelShortcutEnabled = true;
        Assert.True(hotkey.TrySetHotkeyFromString("Alt+F8"));
        Assert.True(hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+P"));
        Assert.True(hotkey.TrySetRecentTranscriptionsHotkeyFromString("Ctrl+R"));
        Assert.True(hotkey.TrySetCopyLastTranscriptionHotkeyFromString("Ctrl+C"));
        Assert.True(hotkey.TrySetTransformSelectionHotkeyFromString("Ctrl+T"));
        hotkey.SetDynamicHotkeys(
            [new PromptActionHotkey("action", KeyCode.VcF10, ModifierMask.LeftMeta)],
            [
                new ProfileHotkey(
                    "profile",
                    KeyCode.VcF11,
                    ModifierMask.LeftMeta,
                    ProfileHotkeyBehavior.StartDictation
                )
            ]
        );
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();
        var configured = backend.LastSet;
        Assert.NotNull(configured);

        hotkey.SetNativeDictationBindingActive(true);
        await backend.WaitUntilSettledAsync();
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.CancelKey);

        hotkey.SetNativeDictationBindingActive(false);
        await backend.WaitUntilSettledAsync();

        var restored = backend.LastSet;
        Assert.NotNull(restored);
        Assert.False(hotkey.NativeDictationBindingActive);
        Assert.Equal(KeyCode.VcF8, restored.DictationKey);
        Assert.Equal(ModifierMask.LeftAlt, restored.DictationModifiers);
        Assert.Equal(KeyCode.VcEscape, restored.CancelKey);
        Assert.Equal(ModifierMask.None, restored.CancelModifiers);
        Assert.True(restored.IsCancelEnabled);
        Assert.Equal(configured.PromptPaletteKey, restored.PromptPaletteKey);
        Assert.Equal(configured.PromptPaletteModifiers, restored.PromptPaletteModifiers);
        Assert.Equal(configured.RecentTranscriptionsKey, restored.RecentTranscriptionsKey);
        Assert.Equal(
            configured.RecentTranscriptionsModifiers,
            restored.RecentTranscriptionsModifiers
        );
        Assert.Equal(
            configured.CopyLastTranscriptionKey,
            restored.CopyLastTranscriptionKey
        );
        Assert.Equal(
            configured.CopyLastTranscriptionModifiers,
            restored.CopyLastTranscriptionModifiers
        );
        Assert.Equal(configured.TransformSelectionKey, restored.TransformSelectionKey);
        Assert.Equal(
            configured.TransformSelectionModifiers,
            restored.TransformSelectionModifiers
        );
        Assert.Equal(configured.PromptActionHotkeys.ToArray(), restored.PromptActionHotkeys);
        Assert.Equal(configured.ProfileHotkeys.ToArray(), restored.ProfileHotkeys);
    }

    [Fact]
    public async Task NativeDictationActiveBeforeInitialize_SuppressesFirstSnapshot()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));

        hotkey.SetNativeDictationBindingActive(true);
        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        Assert.Equal(1, backend.RegisterCount);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
        Assert.Equal(ModifierMask.None, backend.LastSet?.DictationModifiers);
    }

    [Fact]
    public async Task NativeDictationActive_CollisionChecksStillReserveConfiguredChord()
    {
        var backend = new TestShortcutBackend();
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        hotkey.Initialize();
        hotkey.SetNativeDictationBindingActive(true);
        await backend.WaitUntilSettledAsync();

        var accepted = hotkey.TrySetPromptPaletteHotkeyFromString("Ctrl+Shift+Space");

        Assert.False(accepted);
        Assert.Equal("Ctrl+Shift+Space", hotkey.CurrentHotkeyString);
        Assert.Equal("", hotkey.CurrentPromptPaletteHotkeyString);
        Assert.Equal(KeyCode.VcUndefined, backend.LastSet?.DictationKey);
    }

}
