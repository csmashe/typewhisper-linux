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

    private int _disposed;
    private Task? _readLoop;
    private FileStream? _stream;

    public EvdevDeviceReader(
        string path,
        Action<string, int, bool> onKeyEvent,
        Action<string, Exception> onFailure
    )
    {
        Path = path;
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
        var stream = Interlocked.Exchange(ref _stream, null);
        try
        {
            // ReSharper disable once MethodHasAsyncOverload -- synchronous Dispose is deliberate:
            // it closes promptly when no read is in flight; DisposeAsync would defer even that.
            stream?.Dispose();
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
            _stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                0,
                true
            );
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
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        var buf = new byte[InputEvent.SizeBytes];
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
                        n = await stream
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
                if (evt.Type != InputEvent.EvKey)
                {
                    continue;
                }

                if (evt.Value == InputEvent.Repeated)
                {
                    continue;
                }

                _onKeyEvent(Path, evt.Code, evt.Value == InputEvent.Pressed);
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
}
