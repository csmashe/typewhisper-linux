using System.Runtime.InteropServices;
using System.Threading.Channels;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class EvdevDeviceReaderTests
{
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

        var actualFailure = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(snapshotFailure, actualFailure);
        Assert.Equal(1, device.QueryCount);
        Assert.Empty(events.Snapshot());
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

    // ReSharper disable NotAccessedPositionalProperty.Local -- both members are asserted through the record's value equality (Assert.Equal against expected KeyEdge arrays), not read individually
    private readonly record struct KeyEdge(int LinuxKeyCode, bool Pressed);
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (Snapshot().Length < count)
            {
                await _updated.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class FakeInputDevice : IEvdevInputDevice
    {
        private readonly Channel<byte[]> _records = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
        );
        private byte[]? _currentRecord;
        private int _currentOffset;
        private int _queryCount;

        public byte[] Snapshot { get; init; } = Bitmap();
        public Exception? SnapshotFailure { get; init; }
        public int QueryCount => Volatile.Read(ref _queryCount);

        public void Open()
        {
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_currentRecord is null)
            {
                _currentRecord = await _records.Reader.ReadAsync(ct);
                _currentOffset = 0;
            }

            // Deliberately fragment records to exercise the reader's exact-record assembly loop.
            var count = Math.Min(7, Math.Min(buffer.Length, _currentRecord.Length - _currentOffset));
            _currentRecord.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            // ReSharper disable once InvertIf -- inverting would duplicate the `return count` and obscure the reset-on-record-boundary intent
            if (_currentOffset == _currentRecord.Length)
            {
                _currentRecord = null;
                _currentOffset = 0;
            }

            return count;
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
}
