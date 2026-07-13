using SharpHook.Native;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class EvdevGlobalShortcutBackendTests
{
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
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeSessionActivityMonitor(bool inputAllowed) : ISessionActivityMonitor
    {
        private EventHandler? _inputAllowedChanged;

        public bool IsInputAllowed { get; private set; } = inputAllowed;

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

            IsInputAllowed = allowed;
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

        public bool WaitForBlockedEnumeration()
        {
            return _blockedEnumeration.Wait(TimeSpan.FromSeconds(2));
        }

        public void ReleaseBlockedEnumeration()
        {
            _releaseEnumeration.Set();
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
            var reader = new FakeReader(path, onKeyEvent);
            lock (_lock)
            {
                _readers.Add(reader);
            }

            return reader;
        }
    }

    private sealed class FakeReader(string path, Action<string, int, bool> onKeyEvent)
        : IEvdevDeviceReader
    {
        public string Path { get; } = path;
        public bool IsDisposed { get; private set; }

        public bool TryStart()
        {
            return true;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }

        public void Emit(int linuxKeyCode, bool pressed)
        {
            onKeyEvent(Path, linuxKeyCode, pressed);
        }
    }
}
