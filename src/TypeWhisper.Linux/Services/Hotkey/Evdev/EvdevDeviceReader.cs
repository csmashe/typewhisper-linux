using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services.Hotkey.Evdev;

/// <summary>
///     Reads <see cref="InputEvent" /> records from a single
///     <c>/dev/input/eventN</c> device. Opens read-only with
///     <see cref="FileShare.ReadWrite" /> so the kernel keeps delivering to
///     every other reader on the same node.
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

        // Dispose synchronously as a best-effort close before awaiting cancellation. On Unix this
        // stream has a blocking fd, and an in-flight read holds a SafeFileHandle reference, so the
        // actual close is deferred until the next device event releases that reference. Lock and
        // session safety instead comes from EvdevGlobalShortcutBackend.OnKeyEvent: its cached and
        // live input checks, lifecycle generation, and reader-membership guard drop stale events.
        // The generation check alone rejects the old reader after the device is reattached on unlock.
        var inputDevice = Interlocked.Exchange(ref _inputDevice, null);
        try
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous Dispose is deliberate:
            // it closes promptly when no read is in flight; DisposeAsync would defer even that.
            inputDevice?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[EvdevReader] Dispose stream threw: {ex.Message}");
        }

        try
        {
            await _cts.CancelAsync();
        }
        catch
        {
            /* already disposed */
        }

        if (_readLoop is not null)
        {
            try
            {
                // A parked blocking read is expected to outlive this best-effort wait. Its next
                // event is dropped by the backend; cancellation then exits the loop, releasing the
                // final handle reference so the fd closes.
                await _readLoop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch
            {
                /* timeout or cancellation — best effort */
            }
        }

        _cts.Dispose();
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

        _readLoop = Task.Run(() => RunAsync(_cts.Token));
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
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
                        n = await inputDevice
                            .ReadAsync(buf.AsMemory(read, InputEvent.SizeBytes - read), ct)
                            .ConfigureAwait(false);
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
