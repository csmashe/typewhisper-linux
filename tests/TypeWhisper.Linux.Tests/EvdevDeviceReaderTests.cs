using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class EvdevDeviceReaderTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task NormalStream_DeliversEdgesAndSuppressesRepeatsAndDuplicates()
    {
        var device = new FakeInputDevice();
        var events = new EventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());

        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Repeated);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Released);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Released);
        device.Enqueue(InputEvent.EvKey, 30, InputEvent.Pressed); // Processing sentinel: KEY_A.

        await events.WaitForCountAsync(3);

        Assert.Equal(
            [new KeyEdge(57, true), new KeyEdge(57, false), new KeyEdge(30, true)],
            events.Snapshot()
        );
        Assert.Equal(0, device.QueryCount);
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task SynDropped_ReconcilesInDeterministicOrderAndResumesStream()
    {
        var device = new FakeInputDevice
        {
            Snapshot = Bitmap(LinuxKeyMap.KeyLeftctrl, 30), // LeftCtrl + KEY_A.
        };
        var events = new EventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());

        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed); // Remembered terminal.
        device.Enqueue(InputEvent.EvKey, LinuxKeyMap.KeyLeftshift, InputEvent.Pressed);
        await events.WaitForCountAsync(2);

        device.Enqueue(InputEvent.EvSyn, InputEvent.SynDropped, 0);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Released); // Suppressed.
        device.Enqueue(InputEvent.EvSyn, 1, 0); // Suppressed non-report sync data.
        device.Enqueue(InputEvent.EvSyn, InputEvent.SynDropped, 0); // Still recovering.
        device.Enqueue(InputEvent.EvKey, 30, InputEvent.Pressed); // Suppressed.
        device.Enqueue(InputEvent.EvSyn, InputEvent.SynReport, 0);
        device.Enqueue(InputEvent.EvKey, 30, InputEvent.Released); // Normal stream resumes.

        await events.WaitForCountAsync(7);

        Assert.Equal(1, device.QueryCount);
        Assert.Equal(
            [
                new KeyEdge(57, true),
                new KeyEdge(LinuxKeyMap.KeyLeftshift, true),
                new KeyEdge(57, false), // Releases: terminal before modifier.
                new KeyEdge(LinuxKeyMap.KeyLeftshift, false),
                new KeyEdge(LinuxKeyMap.KeyLeftctrl, true), // Presses: modifier before terminal.
                new KeyEdge(30, true),
                new KeyEdge(30, false),
            ],
            events.Snapshot()
        );
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task SynDroppedSnapshot_ReleasesFormerlyHeldTerminalKey()
    {
        var device = new FakeInputDevice { Snapshot = Bitmap() };
        var events = new EventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());

        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed);
        await events.WaitForCountAsync(1);
        device.Enqueue(InputEvent.EvSyn, InputEvent.SynDropped, 0);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed); // Suppressed duplicate data.
        device.Enqueue(InputEvent.EvSyn, InputEvent.SynReport, 0);
        device.Enqueue(InputEvent.EvKey, 30, InputEvent.Pressed); // Processing sentinel.

        await events.WaitForCountAsync(3);

        Assert.Equal(1, device.QueryCount);
        Assert.Equal(
            [new KeyEdge(57, true), new KeyEdge(57, false), new KeyEdge(30, true)],
            events.Snapshot()
        );
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task SynDroppedSnapshotFailure_TerminatesThroughFailureCallback()
    {
        var snapshotFailure = new IOException("Fake EVIOCGKEY failure.");
        var device = new FakeInputDevice { SnapshotFailure = snapshotFailure };
        var events = new EventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());

        device.Enqueue(InputEvent.EvSyn, InputEvent.SynDropped, 0);
        device.Enqueue(InputEvent.EvKey, 57, InputEvent.Pressed);
        device.Enqueue(InputEvent.EvSyn, InputEvent.SynReport, 0);

        var actualFailure = await failure.Task.WaitAsync(s_testGuard);

        Assert.Same(snapshotFailure, actualFailure);
        Assert.Equal(1, device.QueryCount);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task DisposeWhileFakeReadIsParked_ExitsQuietlyWithoutFailure()
    {
        var device = new FakeInputDevice();
        var events = new EventLog();
        var failure = NewFailureSignal();
        var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());
        await device.ReadStarted.WaitAsync(s_testGuard);

        await reader.DisposeAsync().AsTask().WaitAsync(s_testGuard);

        Assert.Empty(events.Snapshot());
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeTimeout_KeepsDevicePublishedUntilWorkerDisposesIt()
    {
        var device = new BlockingReadInputDevice();
        var events = new EventLog();
        var failure = NewFailureSignal();
        var reader = CreateReader(device, events, failure);
        Assert.True(reader.TryStart());
        await device.ReadStarted.WaitAsync(s_testGuard);

        // The read ignores both the token and Wake, so this burns the full 500 ms wake budget.
        await reader.DisposeAsync().AsTask().WaitAsync(s_testGuard);

        // The wake budget lapsed with the worker still parked: the device must stay published
        // and undisposed so the worker's finally can close it. A worker delayed before its
        // field read would otherwise find null, return early, and leak the raw fds.
        Assert.Equal(0, device.DisposeCount);
        Assert.Same(device, ReadPublishedInputDevice(reader));

        device.ReleaseRead();
        await device.Disposed.WaitAsync(s_testGuard);

        Assert.Equal(1, device.DisposeCount);
        Assert.Empty(events.Snapshot());
        Assert.False(failure.Task.IsCompleted);
    }

    [Fact]
    public async Task DisposeWhileParked_WakesLoopAndClosesSlaveFdPromptly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = new LinuxPty();
        var events = new DeviceEventLog();
        var failure = NewFailureSignal();
        var pollBaseline = PollBlockedTaskSnapshot(out _);
        await using var reader = CreateRealReader(pty.SlavePath, events, failure);
        Assert.True(reader.TryStart());
        var readerFd = await pty.WaitForReaderFileDescriptorAsync();

        var partialRecord = EncodeEvents((InputEvent.EvKey, 57, InputEvent.Pressed));
        pty.Write(partialRecord[..7]);
        await pty.WaitForInputToBeConsumedAsync();
        // Consuming the partial record proves the loop ran, but leaves it BETWEEN poll entries:
        // disposing right now can win the cancellation check and exit without ever parking, which
        // silently skips the scenario this test exists for. Wait until the reader task is
        // observably blocked in the poll syscall before disposing.
        await WaitForNewPollParkedTaskAsync(pollBaseline);

        using var traceListener = new LineTraceListener();
        Trace.Listeners.Add(traceListener);
        try
        {
            // ReSharper disable once DisposeOnUsingVariable -- disposing while parked is the behavior under test; the await using re-dispose at scope end is idempotent.
            await reader.DisposeAsync().AsTask().WaitAsync(s_testGuard);
        }
        finally
        {
            Trace.Listeners.Remove(traceListener);
        }

        Assert.NotEqual(pty.SlavePath, LinuxPty.GetFileDescriptorTarget(readerFd));
        Assert.Empty(events.Snapshot());
        Assert.False(failure.Task.IsCompleted);
        Assert.DoesNotContain(
            traceListener.Snapshot(),
            line => line.Contains("Read-loop wake invariant breach", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task RealReader_DeliversPtyInputEvents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = new LinuxPty();
        var events = new DeviceEventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateRealReader(pty.SlavePath, events, failure);
        Assert.True(reader.TryStart());
        var readerFd = await pty.WaitForReaderFileDescriptorAsync();

        pty.Write(
            EncodeEvents(
                (InputEvent.EvKey, 57, InputEvent.Pressed),
                (InputEvent.EvKey, 57, InputEvent.Repeated),
                (InputEvent.EvKey, 57, InputEvent.Pressed),
                (InputEvent.EvKey, 57, InputEvent.Released),
                (InputEvent.EvKey, 57, InputEvent.Released),
                (InputEvent.EvKey, 30, InputEvent.Pressed)
            )
        );

        await events.WaitForCountAsync(3);

        Assert.Equal(
            [
                new DeviceEdge(pty.SlavePath, 57, true),
                new DeviceEdge(pty.SlavePath, 57, false),
                new DeviceEdge(pty.SlavePath, 30, true),
            ],
            events.Snapshot()
        );
        Assert.False(failure.Task.IsCompleted);

        // ReSharper disable once DisposeOnUsingVariable -- the fd must be observably closed before the scope-end dispose; that re-dispose is idempotent.
        await reader.DisposeAsync().AsTask().WaitAsync(s_testGuard);
        Assert.NotEqual(pty.SlavePath, LinuxPty.GetFileDescriptorTarget(readerFd));
    }

    [Fact]
    public async Task PtyMasterHangup_TerminatesReaderAndClosesFd()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = new LinuxPty();
        var events = new DeviceEventLog();
        var failure = NewFailureSignal();
        await using var reader = CreateRealReader(pty.SlavePath, events, failure);
        Assert.True(reader.TryStart());
        var readerFd = await pty.WaitForReaderFileDescriptorAsync();

        pty.CloseMaster();
        var actualFailure = await failure.Task.WaitAsync(s_testGuard);

        // PTYs report master closure as POLLHUP with EOF or, on some kernels, EIO.
        Assert.IsAssignableFrom<IOException>(actualFailure);
        Assert.NotEqual(pty.SlavePath, LinuxPty.GetFileDescriptorTarget(readerFd));
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task PtyMasterHangupWithPendingData_ReportsFailureWithoutDeliveringKeyEdge()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = new LinuxPty();
        var inputDevice = new PausedReadInputDevice(pty.SlavePath);
        var events = new DeviceEventLog();
        var failure = NewFailureSignal();
        await using var reader = new EvdevDeviceReader(
            pty.SlavePath,
            inputDevice,
            (devicePath, keyCode, pressed) =>
                events.Add(new DeviceEdge(devicePath, keyCode, pressed)),
            (_, exception) => failure.TrySetResult(exception)
        );
        Assert.True(reader.TryStart());
        await inputDevice.ReadStarted.WaitAsync(s_testGuard);
        await pty.WaitForReaderFileDescriptorAsync();

        pty.Write(EncodeEvents((InputEvent.EvKey, 57, InputEvent.Pressed)));
        pty.CloseMaster();
        inputDevice.ReleaseRead();

        var actualFailure = await failure.Task.WaitAsync(s_testGuard);

        Assert.IsAssignableFrom<IOException>(actualFailure);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var sequentialPty = new LinuxPty();
        var sequentialReader = CreateRealReader(
            sequentialPty.SlavePath,
            new DeviceEventLog(),
            NewFailureSignal()
        );
        Assert.True(sequentialReader.TryStart());
        var sequentialFd = await sequentialPty.WaitForReaderFileDescriptorAsync();

        await sequentialReader.DisposeAsync().AsTask().WaitAsync(s_testGuard);
        await sequentialReader.DisposeAsync();
        Assert.NotEqual(
            sequentialPty.SlavePath,
            LinuxPty.GetFileDescriptorTarget(sequentialFd)
        );

        using var concurrentPty = new LinuxPty();
        var concurrentReader = CreateRealReader(
            concurrentPty.SlavePath,
            new DeviceEventLog(),
            NewFailureSignal()
        );
        Assert.True(concurrentReader.TryStart());
        var concurrentFd = await concurrentPty.WaitForReaderFileDescriptorAsync();

        var firstDispose = concurrentReader.DisposeAsync().AsTask();
        var secondDispose = concurrentReader.DisposeAsync().AsTask();
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(s_testGuard);
        Assert.NotEqual(
            concurrentPty.SlavePath,
            LinuxPty.GetFileDescriptorTarget(concurrentFd)
        );
    }

    [Fact]
    public async Task MultipleParkedReaders_DisposeConcurrentlyAndCloseBothFds()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var firstPty = new LinuxPty();
        using var secondPty = new LinuxPty();
        await using var firstReader = CreateRealReader(
            firstPty.SlavePath,
            new DeviceEventLog(),
            NewFailureSignal()
        );
        await using var secondReader = CreateRealReader(
            secondPty.SlavePath,
            new DeviceEventLog(),
            NewFailureSignal()
        );
        Assert.True(firstReader.TryStart());
        Assert.True(secondReader.TryStart());
        var firstReaderFd = await firstPty.WaitForReaderFileDescriptorAsync();
        var secondReaderFd = await secondPty.WaitForReaderFileDescriptorAsync();

        var disposeStopwatch = Stopwatch.StartNew();
        // ReSharper disable DisposeOnUsingVariable -- racing both disposals is the behavior under test; the await using re-disposals at scope end are idempotent.
        var firstDispose = firstReader.DisposeAsync().AsTask();
        var secondDispose = secondReader.DisposeAsync().AsTask();
        // ReSharper restore DisposeOnUsingVariable
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(s_testGuard);
        disposeStopwatch.Stop();

        Assert.NotEqual(
            firstPty.SlavePath,
            LinuxPty.GetFileDescriptorTarget(firstReaderFd)
        );
        Assert.NotEqual(
            secondPty.SlavePath,
            LinuxPty.GetFileDescriptorTarget(secondReaderFd)
        );
        // Serialized disposals burn two 500 ms read-loop waits (>= 1000 ms), so stay
        // just under that while giving loaded CI agents as much headroom as possible.
        Assert.True(
            disposeStopwatch.Elapsed < TimeSpan.FromMilliseconds(950),
            $"Concurrent DisposeAsync calls took {disposeStopwatch.Elapsed.TotalMilliseconds:F0} ms."
        );
    }

    [Fact]
    public async Task DisposeBeforeTryStart_DoesNotOpenAnything()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var pty = new LinuxPty();
        var reader = CreateRealReader(
            pty.SlavePath,
            new DeviceEventLog(),
            NewFailureSignal()
        );
        var slaveDescriptorCountBeforeDispose = LinuxPty
            .GetOpenFileDescriptors()
            .Count(fd => LinuxPty.GetFileDescriptorTarget(fd) == pty.SlavePath);

        await reader.DisposeAsync();

        var slaveDescriptorCountAfterDispose = LinuxPty
            .GetOpenFileDescriptors()
            .Count(fd => LinuxPty.GetFileDescriptorTarget(fd) == pty.SlavePath);
        Assert.Equal(slaveDescriptorCountBeforeDispose, slaveDescriptorCountAfterDispose);
        Assert.False(reader.TryStart());
        await reader.DisposeAsync();
    }

    private static EvdevDeviceReader CreateReader(
        IEvdevInputDevice device,
        EventLog events,
        TaskCompletionSource<Exception> failure
    )
    {
        return new EvdevDeviceReader(
            "/fake/input/event0",
            device,
            (_, keyCode, pressed) => events.Add(new KeyEdge(keyCode, pressed)),
            (_, exception) => failure.TrySetResult(exception)
        );
    }

    private static EvdevDeviceReader CreateRealReader(
        string path,
        DeviceEventLog events,
        TaskCompletionSource<Exception> failure
    )
    {
        return new EvdevDeviceReader(
            path,
            (devicePath, keyCode, pressed) =>
                events.Add(new DeviceEdge(devicePath, keyCode, pressed)),
            (_, exception) => failure.TrySetResult(exception)
        );
    }

    // Reflection because the retention constraint — a timed-out DisposeAsync leaves the device
    // published for the worker to capture — has no other observable surface.
    private static IEvdevInputDevice? ReadPublishedInputDevice(EvdevDeviceReader reader)
    {
        var field = typeof(EvdevDeviceReader).GetField(
            "_inputDevice",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        return (IEvdevInputDevice?)field.GetValue(reader);
    }

    private static TaskCompletionSource<Exception> NewFailureSignal()
    {
        return new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    private static byte[] Bitmap(params int[] pressedKeys)
    {
        var result = new byte[EvdevInputDevice.KeyBitmapBytes];
        foreach (var keyCode in pressedKeys)
        {
            result[keyCode / 8] |= (byte)(1 << (keyCode % 8));
        }

        return result;
    }

    private static byte[] EncodeEvents(params (ushort Type, ushort Code, int Value)[] events)
    {
        var result = new byte[events.Length * InputEvent.SizeBytes];
        for (var index = 0; index < events.Length; index++)
        {
            var source = events[index];
            var evt = new InputEvent
            {
                Type = source.Type,
                Code = source.Code,
                Value = source.Value,
            };
            MemoryMarshal.Write(
                result.AsSpan(index * InputEvent.SizeBytes, InputEvent.SizeBytes),
                in evt
            );
        }

        return result;
    }

    // ReSharper disable NotAccessedPositionalProperty.Local -- both members are asserted through the record's value equality (Assert.Equal against expected KeyEdge arrays), not read individually
    private readonly record struct KeyEdge(int LinuxKeyCode, bool Pressed);
    private readonly record struct DeviceEdge(string Path, int LinuxKeyCode, bool Pressed);
    // ReSharper restore NotAccessedPositionalProperty.Local

    private sealed class EventLog
    {
        private readonly List<KeyEdge> _events = [];
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _updated = new(0);

        public void Add(KeyEdge edge)
        {
            lock (_lock)
            {
                _events.Add(edge);
            }

            _updated.Release();
        }

        public KeyEdge[] Snapshot()
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }

        public async Task WaitForCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(s_testGuard);
            while (Snapshot().Length < count)
            {
                await _updated.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class DeviceEventLog
    {
        private readonly List<DeviceEdge> _events = [];
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _updated = new(0);

        public void Add(DeviceEdge edge)
        {
            lock (_lock)
            {
                _events.Add(edge);
            }

            _updated.Release();
        }

        public DeviceEdge[] Snapshot()
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }

        public async Task WaitForCountAsync(int count)
        {
            using var timeout = new CancellationTokenSource(s_testGuard);
            while (Snapshot().Length < count)
            {
                await _updated.WaitAsync(timeout.Token);
            }
        }
    }

    // /proc/self/task/<tid>/wchan names the kernel symbol a blocked task sleeps in (empty or "0"
    // while running). The evdev reader parks in poll(2), whose wait symbols all contain "poll"
    // (do_sys_poll / poll_schedule_timeout / do_sys_ppoll across kernels and libc entry points).
    // sawWchanSymbol reports whether any wchan read exposed a symbol at all, so callers can tell
    // a host that hides wchan (hidepid, wchan-less kernel) apart from a reader that never parked.
    private static HashSet<string> PollBlockedTaskSnapshot(out bool sawWchanSymbol)
    {
        var blocked = new HashSet<string>();
        sawWchanSymbol = false;
        try
        {
            foreach (var taskDir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                try
                {
                    var wchan = File.ReadAllText(Path.Join(taskDir, "wchan")).Trim();
                    if (wchan.Length > 0 && wchan != "0")
                    {
                        sawWchanSymbol = true;
                    }

                    if (wchan.Contains("poll", StringComparison.Ordinal))
                    {
                        blocked.Add(Path.GetFileName(taskDir));
                    }
                }
                catch (IOException)
                {
                    // Task exited between enumeration and read.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Xunit.Sdk.XunitException(
                $"Poll-parking probe cannot enumerate /proc/self/task on this host: {ex.Message}"
            );
        }

        return blocked;
    }

    private static async Task WaitForNewPollParkedTaskAsync(HashSet<string> baseline)
    {
        var sawWchanSymbolEver = false;
        using var timeout = new CancellationTokenSource(s_testGuard);
        while (true)
        {
            var blocked = PollBlockedTaskSnapshot(out var sawWchanSymbol);
            sawWchanSymbolEver |= sawWchanSymbol;
            if (blocked.Any(taskId => !baseline.Contains(taskId)))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException(
                    sawWchanSymbolEver
                        ? "Poll-parking probe timed out: no new task parked in a poll wchan "
                          + $"within {s_testGuard.TotalSeconds:F0}s."
                        : "Poll-parking probe is unsupported on this host: "
                          + "/proc/self/task/*/wchan never exposed a kernel wait symbol "
                          + "(hidepid mount option or a wchan-less kernel)."
                );
            }
        }
    }

    private sealed class LineTraceListener : TraceListener
    {
        private readonly List<string> _lines = [];
        private readonly Lock _lock = new();

        public override void Write(string? message)
        {
            Add(message);
        }

        public override void WriteLine(string? message)
        {
            Add(message);
        }

        public string[] Snapshot()
        {
            lock (_lock)
            {
                return _lines.ToArray();
            }
        }

        private void Add(string? message)
        {
            lock (_lock)
            {
                _lines.Add(message ?? string.Empty);
            }
        }
    }

    private sealed class FakeInputDevice : IEvdevInputDevice
    {
        private readonly Channel<byte[]> _records = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
        );
        private readonly TaskCompletionSource<bool> _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly CancellationTokenSource _wake = new();
        private byte[]? _currentRecord;
        private int _currentOffset;
        private int _queryCount;

        public byte[] Snapshot { get; init; } = Bitmap();
        public Exception? SnapshotFailure { get; init; }
        public int QueryCount => Volatile.Read(ref _queryCount);
        public Task ReadStarted => _readStarted.Task;

        public void Open()
        {
        }

        public int Read(Span<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _readStarted.TrySetResult(true);
            if (_currentRecord is null)
            {
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    _wake.Token
                );
                _currentRecord = _records
                    .Reader.ReadAsync(readCancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                _currentOffset = 0;
            }

            ct.ThrowIfCancellationRequested();
            // Deliberately fragment records to exercise the reader's exact-record assembly loop.
            var count = Math.Min(7, Math.Min(buffer.Length, _currentRecord.Length - _currentOffset));
            _currentRecord.AsSpan(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            // ReSharper disable once InvertIf -- inverting would duplicate the `return count` and obscure the reset-on-record-boundary intent
            if (_currentOffset == _currentRecord.Length)
            {
                _currentRecord = null;
                _currentOffset = 0;
            }

            return count;
        }

        public void Wake()
        {
            _wake.Cancel();
        }

        public byte[] QueryPressedKeyBitmap()
        {
            Interlocked.Increment(ref _queryCount);
            // ReSharper disable once ConvertIfStatementToReturnStatement -- a throw-expression ternary is less readable than this guard clause
            if (SnapshotFailure is not null)
            {
                throw SnapshotFailure;
            }

            return Snapshot.ToArray();
        }

        public void Dispose()
        {
            _wake.Cancel();
            _records.Writer.TryComplete();
        }

        public void Enqueue(ushort type, ushort code, int value)
        {
            var evt = new InputEvent { Type = type, Code = code, Value = value };
            var record = new byte[InputEvent.SizeBytes];
            MemoryMarshal.Write(record, in evt);
            if (!_records.Writer.TryWrite(record))
            {
                throw new InvalidOperationException("The fake input stream is closed.");
            }
        }
    }

    private sealed class BlockingReadInputDevice : IEvdevInputDevice
    {
        private readonly TaskCompletionSource<bool> _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly ManualResetEventSlim _readGate = new(false);
        private readonly TaskCompletionSource<bool> _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _disposeCount;

        public Task ReadStarted => _readStarted.Task;
        public Task Disposed => _disposed.Task;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Open()
        {
        }

        public int Read(Span<byte> buffer, CancellationToken ct)
        {
            _readStarted.TrySetResult(true);
            // Ignores ct and Wake so the parked read outlasts DisposeAsync's wake budget.
            _readGate.Wait(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return 0;
        }

        public void ReleaseRead()
        {
            _readGate.Set();
        }

        public void Wake()
        {
        }

        public byte[] QueryPressedKeyBitmap()
        {
            return Bitmap();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _readGate.Set();
            _disposed.TrySetResult(true);
        }
    }

    private sealed class PausedReadInputDevice(string path) : IEvdevInputDevice
    {
        private readonly EvdevInputDevice _inner = new(path);
        private readonly ManualResetEventSlim _readGate = new(false);
        private readonly TaskCompletionSource<bool> _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task ReadStarted => _readStarted.Task;

        public void Open()
        {
            _inner.Open();
        }

        public int Read(Span<byte> buffer, CancellationToken ct)
        {
            _readStarted.TrySetResult(true);
            _readGate.Wait(ct);
            return _inner.Read(buffer, ct);
        }

        public void ReleaseRead()
        {
            _readGate.Set();
        }

        public void Wake()
        {
            _inner.Wake();
            _readGate.Set();
        }

        public byte[] QueryPressedKeyBitmap()
        {
            return _inner.QueryPressedKeyBitmap();
        }

        public void Dispose()
        {
            _readGate.Set();
            _inner.Dispose();
        }
    }

    private sealed class LinuxPty : IDisposable
    {
        private const int ErrorInterrupted = 4;
        private const int OpenReadWrite = 2;
        private const int OpenNoControllingTerminal = 0x100;
        private const int OpenCloseOnExec = 0x80000;
        private const int SetAttributesNow = 0;
        private const nuint FileInputBytes = 0x541B;

        private SafeFileHandle? _masterHandle;
        private SafeFileHandle? _slaveProbeHandle;

        public LinuxPty()
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("The PTY fixture requires Linux.");
            }

            SafeFileHandle? masterHandle = null;
            SafeFileHandle? slaveProbeHandle = null;
            try
            {
                masterHandle = new SafeFileHandle(OpenPtyMaster(), ownsHandle: true);
                CheckResult(
                    grantpt(masterHandle.DangerousGetHandle().ToInt32()),
                    "grantpt"
                );
                CheckResult(
                    unlockpt(masterHandle.DangerousGetHandle().ToInt32()),
                    "unlockpt"
                );

                var pathBuffer = new byte[256];
                var pathResult = ptsname_r(
                    masterHandle.DangerousGetHandle().ToInt32(),
                    pathBuffer,
                    (nuint)pathBuffer.Length
                );
                if (pathResult != 0)
                {
                    throw new Win32Exception(pathResult, "ptsname_r failed.");
                }

                var terminator = Array.IndexOf(pathBuffer, (byte)0);
                SlavePath = Encoding.UTF8.GetString(
                    pathBuffer,
                    0,
                    terminator >= 0 ? terminator : pathBuffer.Length
                );
                slaveProbeHandle = new SafeFileHandle(OpenSlave(SlavePath), ownsHandle: true);
                ConfigureRaw(slaveProbeHandle);

                _masterHandle = masterHandle;
                _slaveProbeHandle = slaveProbeHandle;
                masterHandle = null;
                slaveProbeHandle = null;
            }
            finally
            {
                slaveProbeHandle?.Dispose();
                masterHandle?.Dispose();
            }
        }

        public string SlavePath { get; }

        public void Write(byte[] bytes)
        {
            var masterHandle = _masterHandle
                               ?? throw new ObjectDisposedException("PTY master");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var remaining = offset == 0 ? bytes : bytes[offset..];
                var written = write(masterHandle, remaining, (nuint)remaining.Length);
                if (written > 0)
                {
                    offset += checked((int)written);
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (written < 0 && error == ErrorInterrupted)
                {
                    continue;
                }

                throw new Win32Exception(
                    error,
                    $"write to PTY master returned {written}."
                );
            }
        }

        public void CloseMaster()
        {
            Interlocked.Exchange(ref _masterHandle, null)?.Dispose();
        }

        public async Task WaitForInputToBeConsumedAsync()
        {
            var slaveProbeHandle = _slaveProbeHandle
                                   ?? throw new ObjectDisposedException("PTY slave probe");
            using var timeout = new CancellationTokenSource(s_testGuard);
            while (true)
            {
                if (ioctl(slaveProbeHandle, FileInputBytes, out var available) < 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        "FIONREAD failed for the PTY slave."
                    );
                }

                if (available == 0)
                {
                    return;
                }

                await Task.Delay(10, timeout.Token);
            }
        }

        public async Task<int> WaitForReaderFileDescriptorAsync()
        {
            var probeFd = (_slaveProbeHandle
                           ?? throw new ObjectDisposedException("PTY slave probe"))
                .DangerousGetHandle()
                .ToInt32();
            using var timeout = new CancellationTokenSource(s_testGuard);
            while (true)
            {
                var readerFds = GetOpenFileDescriptors()
                    .Where(fd => fd != probeFd && GetFileDescriptorTarget(fd) == SlavePath)
                    .ToArray();
                if (readerFds.Length == 1)
                {
                    return readerFds[0];
                }

                await Task.Delay(10, timeout.Token);
            }
        }

        public static int[] GetOpenFileDescriptors()
        {
            return Directory
                .EnumerateFiles("/proc/self/fd")
                .Select(Path.GetFileName)
                .Where(static name => int.TryParse(name, out _))
                .Select(static name => int.Parse(name!))
                .Order()
                .ToArray();
        }

        public static string? GetFileDescriptorTarget(int fd)
        {
            return new FileInfo(Path.Join("/proc/self/fd", fd.ToString())).LinkTarget;
        }

        public void Dispose()
        {
            CloseMaster();
            Interlocked.Exchange(ref _slaveProbeHandle, null)?.Dispose();
        }

        private static int OpenPtyMaster()
        {
            while (true)
            {
                var fd = posix_openpt(
                    OpenReadWrite | OpenNoControllingTerminal | OpenCloseOnExec
                );
                if (fd >= 0)
                {
                    return fd;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorInterrupted)
                {
                    throw new Win32Exception(error, "posix_openpt failed.");
                }
            }
        }

        private static int OpenSlave(string path)
        {
            while (true)
            {
                var fd = open(
                    path,
                    OpenReadWrite | OpenNoControllingTerminal | OpenCloseOnExec,
                    0
                );
                if (fd >= 0)
                {
                    return fd;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorInterrupted)
                {
                    throw new Win32Exception(error, $"Could not open PTY slave {path}.");
                }
            }
        }

        private static void ConfigureRaw(SafeFileHandle slaveHandle)
        {
            // The oversized byte buffer is intentional: libc owns the architecture-specific
            // termios layout, while the test only passes the opaque value between its APIs.
            var attributes = new byte[256];
            CheckResult(tcgetattr(slaveHandle, attributes), "tcgetattr");
            cfmakeraw(attributes);
            CheckResult(tcsetattr(slaveHandle, SetAttributesNow, attributes), "tcsetattr");
        }

        private static void CheckResult(int result, string operation)
        {
            if (result < 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"{operation} failed."
                );
            }
        }

        // This test fixture deliberately uses DllImport so its project does not require AllowUnsafeBlocks.
        [DllImport("libc", SetLastError = true)]
        private static extern int posix_openpt(int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int grantpt(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int unlockpt(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int ptsname_r(int fd, [Out] byte[] buffer, nuint bufferLength);

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags, uint mode);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcgetattr(SafeFileHandle fd, [Out] byte[] attributes);

        [DllImport("libc")]
        private static extern void cfmakeraw([In, Out] byte[] attributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcsetattr(
            SafeFileHandle fd,
            int optionalActions,
            [In] byte[] attributes
        );

        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(SafeFileHandle fd, nuint request, out int result);

        [DllImport("libc", SetLastError = true)]
        private static extern nint write(SafeFileHandle fd, byte[] buffer, nuint count);
    }
}
