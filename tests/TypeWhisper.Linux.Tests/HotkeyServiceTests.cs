using SharpHook.Native;
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
                Success: true,
                BackendId: "test",
                UserMessage: null,
                RequiresToggleMode: true,
                TroubleshootingCommand: null)
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
                Success: false,
                BackendId: "test",
                UserMessage: "boom",
                RequiresToggleMode: false,
                TroubleshootingCommand: null)
        };
        using var hotkey = new HotkeyService(new BackendSelector(() => backend));
        string? observed = null;
        hotkey.HookFailed += (_, msg) => observed = msg;

        hotkey.Initialize();
        await backend.WaitUntilSettledAsync();

        Assert.Equal("boom", observed);
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
            new(Success: true, BackendId: "test", UserMessage: null, RequiresToggleMode: false, TroubleshootingCommand: null);

        public int RegisterCount { get; private set; }
        public GlobalShortcutSet? LastSet { get; private set; }

        public string Id => "test";
        public string DisplayName => "Test";
        public bool SupportsPressRelease => true;
        public bool IsAvailable() => true;

        public event EventHandler? DictationToggleRequested { add { } remove { } }
        public event EventHandler? DictationStartRequested { add { } remove { } }
        public event EventHandler? DictationStopRequested { add { } remove { } }
        public event EventHandler? PromptPaletteRequested { add { } remove { } }
        public event EventHandler? TransformSelectionRequested { add { } remove { } }
        public event EventHandler? RecentTranscriptionsRequested { add { } remove { } }
        public event EventHandler? CopyLastTranscriptionRequested { add { } remove { } }
        public event EventHandler? CancelRequested { add { } remove { } }
        public event EventHandler<string>? Failed { add { } remove { } }

        public Task<GlobalShortcutRegistrationResult> RegisterAsync(GlobalShortcutSet shortcuts, CancellationToken ct)
        {
            Interlocked.Increment(ref _pending);
            RegisterCount++;
            LastSet = shortcuts;
            Interlocked.Decrement(ref _pending);
            return Task.FromResult(NextResult);
        }

        public Task UnregisterAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitUntilSettledAsync()
        {
            // Spin briefly to let the coordinator's chained continuations
            // drain — they run on the thread-pool scheduler so a yield is
            // enough in normal cases; a short timeout guards against hangs.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _pending) == 0)
                {
                    await Task.Delay(20);
                    if (Volatile.Read(ref _pending) == 0) return;
                }
                await Task.Delay(10);
            }
        }
    }
}
