using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reads <see cref="InputEvent" /> records from a single
///     <c>/dev/input/eventN</c> device. The native fd is read-only and does not
///     request exclusive access, so the kernel keeps delivering to every other
///     reader on the same node.
/// </summary>
internal sealed class EvdevDeviceReader : IEvdevDeviceReader
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string, Exception> _onFailure;
    private readonly Action<string, int, bool> _onKeyEvent;
    private readonly HashSet<int> _pressedKeys = [];

    private int _disposed;
    private IEvdevInputDevice? _inputDevice;
    private Task? _readLoop;

    public EvdevDeviceReader(
        string path,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    )
        : this(path, new EvdevInputDevice(path), onKeyEvent, onFailure)
    {
    }

    internal EvdevDeviceReader(
        string path,
        IEvdevInputDevice inputDevice,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    )
    {
        Path = path;
        _inputDevice = inputDevice;
        _onKeyEvent = onKeyEvent;
        _onFailure = onFailure;
    }

    public string Path { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Cancellation prevents a device event that races the wake from being delivered. The
        // eventfd then interrupts poll so the dedicated worker can leave before both native handles
        // are closed. Events already read or queued before the backend lock remain protected by
        // EvdevGlobalShortcutBackend.OnKeyEvent's live-input, generation, and membership guards.
        var inputDevice = Volatile.Read(ref _inputDevice);
        try
        {
            await _cts.CancelAsync();

            try
            {
                inputDevice?.Wake();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevReader] Wake invariant breach: {ex.Message}");
            }

            var readLoop = Volatile.Read(ref _readLoop);
            if (readLoop is not null)
            {
                try
                {
                    // A healthy eventfd wake reclaims the worker promptly. This timeout bounds an
                    // unexpected kernel or interop failure or scheduling starvation; finally still
                    // closes the handles either way.
                    await readLoop
                        .WaitAsync(TimeSpan.FromMilliseconds(500))
                        .ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    Trace.WriteLine(
                        $"[EvdevReader] Read-loop wake invariant breach for {Path}: {ex.Message}"
                    );
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[EvdevReader] Read loop completed unexpectedly for {Path}: {ex.Message}"
                    );
                }
            }
        }
        finally
        {
            try
            {
                Interlocked.CompareExchange(ref _inputDevice, null, inputDevice);

                // The poller uses raw fds (DangerousGetHandle), so closing the handles
                // while the worker is still parked past the wake budget could recycle
                // those descriptor numbers under an in-flight poll/read. Dispose here
                // only when the worker never started or has already returned; a
                // timed-out worker's own finally disposes the device when it exits.
                // Idempotent either way — both paths dispose under the device's lock.
                var readLoop = Volatile.Read(ref _readLoop);
                if (readLoop is null || readLoop.IsCompleted)
                {
                    inputDevice?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevReader] Dispose native handles threw: {ex.Message}");
            }

            _cts.Dispose();
        }
    }

    public bool TryStart()
    {
        try
        {
            var inputDevice = _inputDevice
                              ?? throw new ObjectDisposedException(nameof(EvdevDeviceReader));
            inputDevice.Open();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevReader] Open {Path} failed: {ex.Message}");
            return false;
        }

        Volatile.Write(
            ref _readLoop,
            Task.Factory.StartNew(
                () => Run(_cts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            )
        );
        return true;
    }

    private void Run(CancellationToken ct)
    {
        var inputDevice = _inputDevice;
        if (inputDevice is null)
        {
            return;
        }

        var buf = new byte[InputEvent.SizeBytes];
        var recovering = false;
        Exception? terminating = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < InputEvent.SizeBytes)
                {
                    int n;
                    try
                    {
                        n = inputDevice.Read(
                            buf.AsSpan(read, InputEvent.SizeBytes - read),
                            ct
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException ex)
                    {
                        // Device disappeared (unplug) or kernel closed the
                        // node. Report so the backend can drop us from its
                        // reader map and clear any stuck per-device state.
                        terminating = ex;
                        return;
                    }

                    if (n == 0)
                    {
                        terminating = new EndOfStreamException(
                            $"{Path} reached EOF (device removed?)"
                        );
                        return;
                    }

                    read += n;
                }

                var evt = MemoryMarshal.Read<InputEvent>(buf);
                if (recovering)
                {
                    if (evt is { Type: InputEvent.EvSyn, Code: InputEvent.SynReport })
                    {
                        Reconcile(inputDevice.QueryPressedKeyBitmap());
                        recovering = false;
                    }

                    continue;
                }

                if (evt is { Type: InputEvent.EvSyn, Code: InputEvent.SynDropped })
                {
                    recovering = true;
                    continue;
                }

                if (evt.Type != InputEvent.EvKey)
                {
                    continue;
                }

                switch (evt.Value)
                {
                    case InputEvent.Pressed when _pressedKeys.Add(evt.Code):
                        _onKeyEvent(Path, evt.Code, true);
                        break;
                    case InputEvent.Released when _pressedKeys.Remove(evt.Code):
                        _onKeyEvent(Path, evt.Code, false);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            terminating = ex;
        }
        finally
        {
            try
            {
                inputDevice.Dispose();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[EvdevReader] Dispose native handles threw: {ex.Message}");
            }

            Interlocked.CompareExchange(ref _inputDevice, null, inputDevice);
            if (terminating is not null && Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _onFailure(Path, terminating);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[EvdevReader] onFailure callback threw: {ex.Message}");
                }
            }
        }
    }

    private void Reconcile(ReadOnlySpan<byte> keyBits)
    {
        var snapshot = new HashSet<int>();
        var highestKey = Math.Min(EvdevInputDevice.KeyMax, keyBits.Length * 8 - 1);
        for (var keyCode = 0; keyCode <= highestKey; keyCode++)
        {
            if ((keyBits[keyCode / 8] & (1 << (keyCode % 8))) != 0)
            {
                snapshot.Add(keyCode);
            }
        }

        // Clear terminal-key guards before modifiers, then rebuild modifiers before terminal keys
        // so reconstructed chords carry the same aggregate modifier snapshot as normal input.
        foreach (
            var keyCode in _pressedKeys
                .Except(snapshot)
                .OrderBy(static code => LinuxKeyMap.IsModifier(code) ? 1 : 0)
                .ThenBy(static code => code)
        )
        {
            _onKeyEvent(Path, keyCode, false);
        }

        foreach (
            var keyCode in snapshot
                .Except(_pressedKeys)
                .OrderBy(static code => LinuxKeyMap.IsModifier(code) ? 0 : 1)
                .ThenBy(static code => code)
        )
        {
            _onKeyEvent(Path, keyCode, true);
        }

        // Keep the pre-drop set intact until every logical edge has been accepted. If a callback
        // throws, the failure path detaches the reader and the backend subtracts what it received.
        _pressedKeys.Clear();
        _pressedKeys.UnionWith(snapshot);
    }
}
