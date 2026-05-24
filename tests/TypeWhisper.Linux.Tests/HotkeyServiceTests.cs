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

        public event EventHandler<string>? Failed
        {
            add { }
            remove { }
        }

        public void RaisePromptAction(string actionId)
        {
            PromptActionRequested?.Invoke(this, actionId);
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