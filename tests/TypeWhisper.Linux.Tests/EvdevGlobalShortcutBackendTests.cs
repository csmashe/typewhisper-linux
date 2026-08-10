using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class EvdevGlobalShortcutBackendTests
{
    [Fact]
    public async Task SameModifierHeldByTwoReaders_OneReleaseKeepsRemainingChordUsable()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1", "/dev/input/event2");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);
        var toggleCount = 0;
        backend.DictationToggleRequested += (_, _) => toggleCount++;

        var shortcuts = DefaultShortcuts() with
        {
            DictationModifiers = ModifierMask.LeftCtrl,
        };
        Assert.True((await backend.RegisterAsync(shortcuts, CancellationToken.None)).Success);
        var readers = factory.Readers;

        readers[0].Emit(LinuxKeyMap.KeyLeftctrl, true);
        readers[1].Emit(LinuxKeyMap.KeyLeftctrl, true);
        readers[0].Emit(LinuxKeyMap.KeyLeftctrl, false);
        readers[1].Emit(57, true); // KEY_SPACE

        // A global bitmask clears LeftCtrl on reader 0's release and misses this chord.
        Assert.Equal(1, toggleCount);
    }

    [Fact]
    public async Task ReaderFailure_SubtractsOnlyFailedReaderState()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1", "/dev/input/event2");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);
        var toggleCount = 0;
        backend.DictationToggleRequested += (_, _) => toggleCount++;

        var shortcuts = DefaultShortcuts() with
        {
            DictationModifiers = ModifierMask.LeftCtrl,
        };
        Assert.True((await backend.RegisterAsync(shortcuts, CancellationToken.None)).Success);
        var readers = factory.Readers;

        readers[0].Emit(LinuxKeyMap.KeyLeftshift, true);
        readers[1].Emit(LinuxKeyMap.KeyLeftctrl, true);
        readers[0].Fail();
        readers[1].Emit(57, true); // KEY_SPACE

        // Clearing the old process-wide mask on any failure loses reader 1's held LeftCtrl.
        Assert.Equal(1, toggleCount);
        Assert.True(readers[0].IsDisposed);
    }

    [Fact]
    public async Task ReaderFailure_ReleasesSoleModifierAndPreventsFalseLaterChord()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1", "/dev/input/event2");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);
        var paletteCount = 0;
        var toggleCount = 0;
        backend.PromptPaletteRequested += (_, _) => paletteCount++;
        backend.DictationToggleRequested += (_, _) => toggleCount++;

        var shortcuts = DefaultShortcuts() with
        {
            DictationModifiers = ModifierMask.LeftCtrl,
            PromptPaletteKey = KeyCode.VcLeftControl,
            PromptPaletteModifiers = ModifierMask.None,
        };
        Assert.True((await backend.RegisterAsync(shortcuts, CancellationToken.None)).Success);
        var readers = factory.Readers;

        readers[0].Emit(LinuxKeyMap.KeyLeftctrl, true);
        Assert.Equal(0, paletteCount); // Selection workflows remain release-gated.
        readers[0].Fail();
        readers[1].Emit(57, true); // KEY_SPACE

        // The palette proves detach emitted the modifier release; clearing only a mask leaves the
        // workflow pending. The later Space proves the lost modifier no longer forms a chord.
        Assert.Equal(1, paletteCount);
        Assert.Equal(0, toggleCount);
    }

    [Fact]
    public async Task ReaderFailure_ReleasesHeldDictationKeyUsingPressTimeMode()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1", "/dev/input/event2");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);
        var startCount = 0;
        var stopCount = 0;
        var toggleCount = 0;
        backend.DictationStartRequested += (_, _) => startCount++;
        backend.DictationStopRequested += (_, _) => stopCount++;
        backend.DictationToggleRequested += (_, _) => toggleCount++;

        var pushToTalk = DefaultShortcuts() with
        {
            DictationModifiers = ModifierMask.None,
            Mode = RecordingMode.PushToTalk,
        };
        Assert.True((await backend.RegisterAsync(pushToTalk, CancellationToken.None)).Success);
        var readers = factory.Readers;

        readers[0].Emit(57, true); // KEY_SPACE
        Assert.Equal(1, startCount);

        var rebound = pushToTalk with { Mode = RecordingMode.Toggle };
        Assert.True((await backend.RegisterAsync(rebound, CancellationToken.None)).Success);
        readers[0].Fail();
        readers[1].Emit(57, true);

        // Detach must release the captured PTT hold even after the mode changes, and the next
        // aggregate press must not be stranded behind the old dispatcher's key-down guard.
        Assert.Equal(1, stopCount);
        Assert.Equal(1, toggleCount);
    }

    [Fact]
    public async Task Lock_DisposesAllReaders_ResetsState_AndDropsStaleEvents()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1", "/dev/input/event2");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);
        var toggleCount = 0;
        backend.DictationToggleRequested += (_, _) => toggleCount++;

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);
        Assert.True(result.Success);
        var originalReaders = factory.Readers;
        Assert.Equal(2, originalReaders.Length);

        // Leave Ctrl down. Closing the devices means its release will never arrive.
        originalReaders[0].Emit(LinuxKeyMap.KeyLeftctrl, true);
        monitor.SetInputAllowed(false);

        Assert.All(originalReaders, reader => Assert.True(reader.IsDisposed));

        // Simulate callbacks that were already queued when the fd was closed.
        originalReaders[0].Emit(LinuxKeyMap.KeyLeftshift, true);
        originalReaders[0].Emit(57, true); // KEY_SPACE
        Assert.Equal(0, toggleCount);

        monitor.SetInputAllowed(true);
        await WaitUntilAsync(() => factory.Readers.Length == 4);
        var reopened = factory.Readers[2];

        // Ctrl from before lock must not remain in the aggregate after reopen.
        reopened.Emit(LinuxKeyMap.KeyLeftshift, true);
        reopened.Emit(57, true);
        Assert.Equal(0, toggleCount);

        reopened.Emit(57, false);
        reopened.Emit(LinuxKeyMap.KeyLeftshift, false);
        reopened.Emit(LinuxKeyMap.KeyLeftctrl, true);
        reopened.Emit(LinuxKeyMap.KeyLeftshift, true);
        reopened.Emit(57, true);
        Assert.Equal(1, toggleCount);
    }

    [Fact]
    public async Task LockReset_CompletesBeforeUnlockedReaderAttaches()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var resetBlocker = new BlockingResetHook();
        var backend = new EvdevGlobalShortcutBackend(
            monitor,
            enumerator,
            factory,
            beforeSessionResetState: resetBlocker.Block
        );
        Task? lockTransition = null;

        try
        {
            var startCount = 0;
            var stopCount = 0;
            var discardCount = 0;
            backend.DictationStartRequested += (_, _) => startCount++;
            backend.DictationStopRequested += (_, _) => stopCount++;
            backend.DictationDiscardRequested += (_, _) => discardCount++;

            var shortcuts = DefaultShortcuts() with
            {
                DictationModifiers = ModifierMask.None,
                Mode = RecordingMode.PushToTalk,
            };
            Assert.True((await backend.RegisterAsync(shortcuts, CancellationToken.None)).Success);
            Assert.Single(factory.Readers);

            enumerator.BlockNextEnumeration();
            lockTransition = Task.Run(() => monitor.SetInputAllowed(false));
            Assert.True(resetBlocker.WaitUntilBlocked());

            monitor.SetInputAllowed(true);

            // The unlock callback has run, but its reopen must remain behind the parked reset.
            Assert.False(
                enumerator.WaitForBlockedEnumeration(TimeSpan.FromMilliseconds(100))
            );
            Assert.Equal(1, enumerator.EnumerationCount);
            Assert.Single(factory.Readers);

            resetBlocker.Release();
            await lockTransition.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(enumerator.WaitForBlockedEnumeration());
            enumerator.ReleaseBlockedEnumeration();
            await WaitUntilAsync(() => factory.Readers.Length == 2);

            var reopened = factory.Readers[1];
            reopened.Emit(57, true); // KEY_SPACE
            reopened.Emit(57, false);

            // A reset delayed past this fresh PTT press would clear its hold and lose the stop.
            Assert.Equal(1, discardCount);
            Assert.Equal(1, startCount);
            Assert.Equal(1, stopCount);
        }
        finally
        {
            resetBlocker.Release();
            enumerator.ReleaseBlockedEnumeration();
            if (lockTransition is not null)
            {
                await lockTransition.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await backend.DisposeAsync();
        }
    }

    [Fact]
    public async Task Rescan_WhileResetBarrierPending_DoesNotAttach()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var resetBlocker = new BlockingResetHook();
        var backend = new EvdevGlobalShortcutBackend(
            monitor,
            enumerator,
            factory,
            beforeSessionResetState: resetBlocker.Block
        );
        Task? lockTransition = null;

        try
        {
            Assert.True(
                (await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None)).Success
            );
            Assert.Single(factory.Readers);

            enumerator.Add("/dev/input/event2");
            lockTransition = Task.Run(() => monitor.SetInputAllowed(false));
            Assert.True(resetBlocker.WaitUntilBlocked());

            monitor.SetInputAllowed(true);

            Assert.False(backend.Rescan());
            Assert.Single(factory.Readers);

            resetBlocker.Release();
            await lockTransition.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => factory.Readers.Length == 3);

            Assert.Equal(2, factory.Readers.Count(reader => !reader.IsDisposed));
        }
        finally
        {
            resetBlocker.ReleaseAll();
            if (lockTransition is not null)
            {
                await lockTransition.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await backend.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unlock_AwaitsAllOutstandingResets_AcrossOverlappingCycles()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var resetBlocker = new BlockingResetHook(2);
        var backend = new EvdevGlobalShortcutBackend(
            monitor,
            enumerator,
            factory,
            beforeSessionResetState: resetBlocker.Block
        );
        Task? lockA = null;
        Task? lockB = null;

        try
        {
            var startCount = 0;
            var stopCount = 0;
            var discardCount = 0;
            backend.DictationStartRequested += (_, _) => startCount++;
            backend.DictationStopRequested += (_, _) => stopCount++;
            backend.DictationDiscardRequested += (_, _) => discardCount++;

            var shortcuts = DefaultShortcuts() with
            {
                DictationModifiers = ModifierMask.None,
                Mode = RecordingMode.PushToTalk,
            };
            Assert.True((await backend.RegisterAsync(shortcuts, CancellationToken.None)).Success);
            Assert.Single(factory.Readers);

            lockA = Task.Run(() => monitor.SetInputAllowed(false));
            // ReSharper disable once RedundantArgumentDefaultValue -- explicit cycle index pairs with the (1) calls; naming both cycles is what makes the ordering readable.
            Assert.True(resetBlocker.WaitUntilBlocked(0));
            monitor.SetInputAllowed(true);

            lockB = Task.Run(() => monitor.SetInputAllowed(false));
            Assert.True(resetBlocker.WaitUntilBlocked(1));
            monitor.SetInputAllowed(true);

            enumerator.BlockNextEnumeration();
            resetBlocker.Release(1);
            await lockB.WaitAsync(TimeSpan.FromSeconds(2));

            // Reopen B must still wait for reset A, not merely the latest reset B.
            Assert.False(
                enumerator.WaitForBlockedEnumeration(TimeSpan.FromMilliseconds(100))
            );
            Assert.Single(factory.Readers);

            // ReSharper disable once RedundantArgumentDefaultValue -- explicit cycle index pairs with Release(1) above; releasing B then A in that order is the assertion.
            resetBlocker.Release(0);
            await lockA.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(enumerator.WaitForBlockedEnumeration());
            enumerator.ReleaseBlockedEnumeration();
            await WaitUntilAsync(() => factory.Readers.Length == 2);

            var reopened = Assert.Single(factory.Readers, reader => !reader.IsDisposed);
            reopened.Emit(57, true); // KEY_SPACE
            reopened.Emit(57, false);

            Assert.Equal(2, discardCount);
            Assert.Equal(1, startCount);
            Assert.Equal(1, stopCount);
        }
        finally
        {
            resetBlocker.ReleaseAll();
            enumerator.ReleaseBlockedEnumeration();
            if (lockA is not null)
            {
                await lockA.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (lockB is not null)
            {
                await lockB.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await backend.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnlockWithoutPriorLock_AttachesImmediately()
    {
        var monitor = new FakeSessionActivityMonitor(false);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        Assert.True((await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None)).Success);
        Assert.Empty(factory.Readers);

        monitor.SetInputAllowed(true);

        await WaitUntilAsync(() => factory.Readers.Length == 1);
        Assert.Equal(1, enumerator.EnumerationCount);
        Assert.False(factory.Readers[0].IsDisposed);
    }

    [Fact]
    public async Task RapidLockUnlockCycles_NeverDeadlockAndAlwaysReattach()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        Assert.True((await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None)).Success);

        var transitions = Task.Run(async () =>
        {
            for (var cycle = 0; cycle < 3; cycle++)
            {
                monitor.SetInputAllowed(false);
                monitor.SetInputAllowed(true);
                await WaitUntilAsync(
                    () => factory.Readers.Count(reader => !reader.IsDisposed) == 1
                );
            }
        });

        await transitions.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, enumerator.EnumerationCount);
        Assert.Single(factory.Readers, reader => !reader.IsDisposed);
    }

    [Fact]
    public async Task Dispose_WithPendingResetBarrier_VetoesQueuedReopen()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var resetBlocker = new BlockingResetHook();
        var backend = new EvdevGlobalShortcutBackend(
            monitor,
            enumerator,
            factory,
            beforeSessionResetState: resetBlocker.Block
        );
        Task? lockTransition = null;

        try
        {
            Assert.True(
                (await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None)).Success
            );
            lockTransition = Task.Run(() => monitor.SetInputAllowed(false));
            Assert.True(resetBlocker.WaitUntilBlocked());

            // Queue an unlock behind the still-pending reset, then dispose while it is waiting.
            monitor.SetInputAllowed(true);
            await backend.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, monitor.SubscriberCount);
            Assert.Single(factory.Readers);
        }
        finally
        {
            resetBlocker.Release();
            if (lockTransition is not null)
            {
                await lockTransition.WaitAsync(TimeSpan.FromSeconds(2));
            }

            await backend.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unlock_ReenumeratesAndPicksUpDeviceAddedWhileLocked()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, enumerator.EnumerationCount);

        monitor.SetInputAllowed(false);
        enumerator.Add("/dev/input/event2");
        monitor.SetInputAllowed(true);

        await WaitUntilAsync(() => factory.Readers.Length == 3);
        Assert.Equal(2, enumerator.EnumerationCount);
        Assert.Equal(
            ["/dev/input/event1", "/dev/input/event2"],
            factory.Readers.Skip(1).Select(reader => reader.Path).Order().ToArray()
        );
    }

    [Fact]
    public async Task SessionInactiveThenActive_ClosesAndReopensReaders()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);
        Assert.True(result.Success);
        var firstReader = Assert.Single(factory.Readers);

        // Session-inactive surfaces as the same IsInputAllowed=false transition as locked.
        monitor.SetInputAllowed(false);
        Assert.True(firstReader.IsDisposed);

        monitor.SetInputAllowed(true);
        await WaitUntilAsync(() => factory.Readers.Length == 2);
        Assert.False(factory.Readers[1].IsDisposed);
        Assert.Equal(2, enumerator.EnumerationCount);
    }

    [Fact]
    public async Task RegistrationWhileLocked_DefersOpeningReadersUntilUnlock()
    {
        var monitor = new FakeSessionActivityMonitor(false);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        await using var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(factory.Readers);
        Assert.Equal(0, enumerator.EnumerationCount);

        monitor.SetInputAllowed(true);
        await WaitUntilAsync(() => factory.Readers.Length == 1);
        Assert.Equal(1, enumerator.EnumerationCount);
    }

    [Fact]
    public async Task MonitorUnavailable_PreservesLegacyOpenAndDisposeBehavior()
    {
        // An unavailable production monitor permanently reports allowed; this fake models that
        // fallback without attempting to connect to the machine's real system bus.
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);
        Assert.True(result.Success);
        var reader = Assert.Single(factory.Readers);
        Assert.False(reader.IsDisposed);

        await backend.DisposeAsync();

        Assert.True(reader.IsDisposed);
        Assert.Equal(0, monitor.SubscriberCount);
    }

    [Fact]
    public async Task DisposeWhileLocked_PreventsStaleUnlockReopenFromAttachingReaders()
    {
        var monitor = new FakeSessionActivityMonitor(true);
        var enumerator = new FakeKeyboardEnumerator("/dev/input/event1");
        var factory = new FakeReaderFactory();
        var backend = new EvdevGlobalShortcutBackend(monitor, enumerator, factory);

        var result = await backend.RegisterAsync(DefaultShortcuts(), CancellationToken.None);
        Assert.True(result.Success);
        var originalReader = Assert.Single(factory.Readers);
        monitor.SetInputAllowed(false);
        Assert.True(originalReader.IsDisposed);

        enumerator.BlockNextEnumeration();
        monitor.SetInputAllowed(true);
        Assert.True(enumerator.WaitForBlockedEnumeration());

        // A newer lock and disposal both advance the lifecycle generation while the old unlock's
        // enumeration is still in flight. Releasing it must not resurrect a reader.
        monitor.SetInputAllowed(false);
        await backend.DisposeAsync();
        enumerator.ReleaseBlockedEnumeration();

        await Task.Delay(50);
        Assert.Single(factory.Readers);
        Assert.All(factory.Readers, reader => Assert.True(reader.IsDisposed));
        Assert.Equal(0, monitor.SubscriberCount);

        monitor.SetInputAllowed(true);
        await Task.Delay(50);
        Assert.Single(factory.Readers);
    }

    private static GlobalShortcutSet DefaultShortcuts()
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
            [],
            []
        );
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (!predicate())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // A bare "task was canceled" says nothing about which condition never held.
            Assert.Fail("Timed out after 2s waiting for the predicate to become true.");
        }
    }

    private sealed class FakeSessionActivityMonitor(bool inputAllowed) : ISessionActivityMonitor
    {
        private EventHandler? _inputAllowedChanged;

        // Written by the test thread, read by the backend's reader threads and WaitUntilAsync's
        // polling loop, so both sides go through Volatile.
        private bool _isInputAllowed = inputAllowed;

        public bool IsInputAllowed => Volatile.Read(ref _isInputAllowed);

        public int SubscriberCount => _inputAllowedChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler? InputAllowedChanged
        {
            add => _inputAllowedChanged += value;
            remove => _inputAllowedChanged -= value;
        }

        public Task InitializeAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void SetInputAllowed(bool allowed)
        {
            if (IsInputAllowed == allowed)
            {
                return;
            }

            Volatile.Write(ref _isInputAllowed, allowed);
            _inputAllowedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeKeyboardEnumerator(params string[] paths) : IEvdevKeyboardEnumerator
    {
        private readonly ManualResetEventSlim _blockedEnumeration = new();
        private readonly ManualResetEventSlim _releaseEnumeration = new();
        private readonly Lock _lock = new();
        private readonly List<string> _paths = [.. paths];
        private int _blockNext;
        private int _enumerationCount;

        public int EnumerationCount => Volatile.Read(ref _enumerationCount);

        public IEnumerable<string> EnumerateKeyboards()
        {
            Interlocked.Increment(ref _enumerationCount);
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _blockedEnumeration.Set();
                _releaseEnumeration.Wait(TimeSpan.FromSeconds(2));
            }

            lock (_lock)
            {
                return _paths.ToArray();
            }
        }

        public bool Exists(string path)
        {
            lock (_lock)
            {
                return _paths.Contains(path, StringComparer.Ordinal);
            }
        }

        public void Add(string path)
        {
            lock (_lock)
            {
                _paths.Add(path);
            }
        }

        public void BlockNextEnumeration()
        {
            _blockedEnumeration.Reset();
            _releaseEnumeration.Reset();
            Interlocked.Exchange(ref _blockNext, 1);
        }

        public bool WaitForBlockedEnumeration(TimeSpan? timeout = null)
        {
            return _blockedEnumeration.Wait(timeout ?? TimeSpan.FromSeconds(2));
        }

        public void ReleaseBlockedEnumeration()
        {
            _releaseEnumeration.Set();
        }
    }

    private sealed class BlockingResetHook
    {
        private readonly ManualResetEventSlim[] _blocked;
        private readonly ManualResetEventSlim[] _release;
        private int _nextInvocation;

        public BlockingResetHook(int invocationCount = 1)
        {
            _blocked = Enumerable.Range(0, invocationCount)
                .Select(_ => new ManualResetEventSlim())
                .ToArray();
            _release = Enumerable.Range(0, invocationCount)
                .Select(_ => new ManualResetEventSlim())
                .ToArray();
        }

        public void Block()
        {
            var invocation = Interlocked.Increment(ref _nextInvocation) - 1;
            if (invocation >= _blocked.Length)
            {
                return;
            }

            _blocked[invocation].Set();
            if (!_release[invocation].Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    $"Timed out waiting to release reset hook invocation {invocation}."
                );
            }
        }

        public bool WaitUntilBlocked(int invocation = 0)
        {
            return _blocked[invocation].Wait(TimeSpan.FromSeconds(2));
        }

        public void Release(int invocation = 0)
        {
            _release[invocation].Set();
        }

        public void ReleaseAll()
        {
            foreach (var release in _release)
            {
                release.Set();
            }
        }
    }

    private sealed class FakeReaderFactory : IEvdevDeviceReaderFactory
    {
        private readonly Lock _lock = new();
        private readonly List<FakeReader> _readers = [];

        public FakeReader[] Readers
        {
            get
            {
                lock (_lock)
                {
                    return _readers.ToArray();
                }
            }
        }

        public IEvdevDeviceReader Create(
            string path,
            Action<string, int, bool> onKeyEvent,
            Action<string, Exception> onFailure
        )
        {
            var reader = new FakeReader(path, onKeyEvent, onFailure);
            lock (_lock)
            {
                _readers.Add(reader);
            }

            return reader;
        }
    }

    private sealed class FakeReader(
        string path,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    )
        : IEvdevDeviceReader
    {
        // Set on whichever thread tears the backend down, polled from the test thread.
        private bool _isDisposed;

        public string Path { get; } = path;
        public bool IsDisposed => Volatile.Read(ref _isDisposed);

        public bool TryStart()
        {
            return true;
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _isDisposed, true);
            return ValueTask.CompletedTask;
        }

        public void Emit(int linuxKeyCode, bool pressed)
        {
            onKeyEvent(Path, linuxKeyCode, pressed);
        }

        public void Fail(Exception? exception = null)
        {
            onFailure(Path, exception ?? new IOException("Fake evdev reader failure."));
        }
    }
}
